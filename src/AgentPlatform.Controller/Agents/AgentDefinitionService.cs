using System.Text.Json;
using AgentPlatform.Controller.Contracts;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Json;
using AgentPlatform.Core.Policy;
using AgentPlatform.Core.Validation;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Persistence.Entities;
using AgentPlatform.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Controller.Agents;

/// <summary>Ошибка валидации конфигурации агента (HTTP 400/409 на уровне endpoint-а).</summary>
public sealed class AgentValidationException(string code, string message) : Exception(message)
{
    /// <summary>Машиночитаемый код ошибки для API/UI.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// CRUD агентов Controller API. Версии неизменяемы: изменение агента публикует
/// новую версию (<c>agent_versions</c>), <c>agents.current_version</c> переключается.
/// <para>
/// Политики тулов проверяются <see cref="ToolSettingsValidator"/>: повышение
/// манифестной политики отклоняется до сохранения (клиент проверяет то же правило).
/// Включённые тулы версии фиксируются в enabled_tools, настройки политики/гейта —
/// в tool_settings установки.
/// </para>
/// </summary>
public sealed class AgentDefinitionService(
    AgentPlatformDbContext context,
    TenantContext tenantContext)
{
    private const string PublishedStatus = "published";
    private const string ActiveInstallationStatus = "active";

    private readonly AgentPlatformDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    private readonly TenantContext _tenantContext =
        tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));

    /// <summary>Список агентов тенанта с параметрами текущих версий.</summary>
    public async Task<IReadOnlyList<AgentResponse>> ListAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);

        var agents = await _context.Agents
            .AsNoTracking()
            .Where(agent => agent.TenantId == tenantId)
            .OrderBy(agent => agent.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var result = new List<AgentResponse>(agents.Count);

        foreach (var agent in agents)
        {
            var version = await FindVersionAsync(agent.Id, agent.CurrentVersion, cancellationToken)
                .ConfigureAwait(false);

            if (version is not null)
            {
                result.Add(await ToResponseAsync(tenantId, agent, version, cancellationToken)
                    .ConfigureAwait(false));
            }
        }

        return result;
    }

    /// <summary>Агент по идентификатору (null — не найден в тенанте).</summary>
    public async Task<AgentResponse?> GetAsync(
        Guid tenantId,
        Guid agentId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);

        var agent = await _context.Agents
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.TenantId == tenantId && candidate.Id == agentId,
                cancellationToken)
            .ConfigureAwait(false);

        if (agent is null)
        {
            return null;
        }

        var version = await FindVersionAsync(agent.Id, agent.CurrentVersion, cancellationToken)
            .ConfigureAwait(false);

        return version is null
            ? null
            : await ToResponseAsync(tenantId, agent, version, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Создаёт агента и опубликованную версию 1.</summary>
    public async Task<AgentResponse> CreateAsync(
        Guid tenantId,
        AgentUpsertRequest request,
        Guid? actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireTenant(tenantId);
        ValidateRequest(request);

        var name = request.Name.Trim();

        if (await _context.Agents.AnyAsync(
                agent => agent.TenantId == tenantId && agent.Name == name,
                cancellationToken).ConfigureAwait(false))
        {
            throw new AgentValidationException(
                "agent_name_conflict",
                $"Agent name '{name}' already exists in the tenant.");
        }

        var resolvedActor = await ResolveActorAsync(tenantId, actorId, cancellationToken)
            .ConfigureAwait(false);

        var plan = await BuildToolsAsync(tenantId, request.Tools, cancellationToken).ConfigureAwait(false);

        var agent = new Agent
        {
            TenantId = tenantId,
            Name = name,
            CurrentVersion = 1
        };

        _context.Agents.Add(agent);
        _context.AgentVersions.Add(CreateVersion(agent, 1, request, plan.EnabledToolsJson, resolvedActor));
        await ApplyToolSettingsAsync(plan.Tools, cancellationToken).ConfigureAwait(false);

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var version = await FindVersionAsync(agent.Id, agent.CurrentVersion, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Agent version 1 was not saved.");

        return await ToResponseAsync(tenantId, agent, version, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Публикует новую версию агента (номер = max + 1). Старые сессии остаются
    /// на закреплённой версии (version pinning).
    /// </summary>
    public async Task<AgentResponse?> UpdateAsync(
        Guid tenantId,
        Guid agentId,
        AgentUpsertRequest request,
        Guid? actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireTenant(tenantId);
        ValidateRequest(request);

        var agent = await _context.Agents
            .FirstOrDefaultAsync(
                candidate => candidate.TenantId == tenantId && candidate.Id == agentId,
                cancellationToken)
            .ConfigureAwait(false);

        if (agent is null)
        {
            return null;
        }

        var name = request.Name.Trim();

        if (await _context.Agents.AnyAsync(
                candidate => candidate.TenantId == tenantId
                    && candidate.Name == name
                    && candidate.Id != agentId,
                cancellationToken).ConfigureAwait(false))
        {
            throw new AgentValidationException(
                "agent_name_conflict",
                $"Agent name '{name}' already exists in the tenant.");
        }

        var resolvedActor = await ResolveActorAsync(tenantId, actorId, cancellationToken)
            .ConfigureAwait(false);

        var plan = await BuildToolsAsync(tenantId, request.Tools, cancellationToken).ConfigureAwait(false);

        var maxVersion = await _context.AgentVersions
            .Where(version => version.AgentId == agentId)
            .MaxAsync(version => (int?)version.Version, cancellationToken)
            .ConfigureAwait(false) ?? 0;

        var nextVersion = maxVersion + 1;

        agent.Name = name;
        agent.CurrentVersion = nextVersion;

        _context.AgentVersions.Add(CreateVersion(agent, nextVersion, request, plan.EnabledToolsJson, resolvedActor));
        await ApplyToolSettingsAsync(plan.Tools, cancellationToken).ConfigureAwait(false);

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var version = await FindVersionAsync(agent.Id, agent.CurrentVersion, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Agent version was not saved.");

        return await ToResponseAsync(tenantId, agent, version, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Удаляет агента, если нет сессий и расписаний (иначе 409).</summary>
    public async Task<bool> DeleteAsync(
        Guid tenantId,
        Guid agentId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);

        var agent = await _context.Agents
            .FirstOrDefaultAsync(
                candidate => candidate.TenantId == tenantId && candidate.Id == agentId,
                cancellationToken)
            .ConfigureAwait(false);

        if (agent is null)
        {
            return false;
        }

        if (await _context.Sessions.AnyAsync(
                session => session.TenantId == tenantId && session.AgentId == agentId,
                cancellationToken).ConfigureAwait(false))
        {
            throw new AgentValidationException(
                "agent_has_sessions",
                "Agent has dialog sessions and cannot be deleted; publish a new version instead.");
        }

        if (await _context.ScheduledRuns.AnyAsync(
                run => run.TenantId == tenantId && run.AgentId == agentId,
                cancellationToken).ConfigureAwait(false))
        {
            throw new AgentValidationException(
                "agent_has_schedules",
                "Agent has scheduled runs and cannot be deleted; delete the schedules first.");
        }

        var versions = await _context.AgentVersions
            .Where(version => version.AgentId == agentId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        _context.AgentVersions.RemoveRange(versions);
        _context.Agents.Remove(agent);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    private async Task<ToolBuildResult> BuildToolsAsync(
        Guid tenantId,
        IReadOnlyList<AgentToolSelectionRequest>? selections,
        CancellationToken cancellationToken)
    {
        if (selections is null || selections.Count == 0)
        {
            return new ToolBuildResult("[]", []);
        }

        var duplicate = selections
            .GroupBy(selection => (selection.InstallationId, selection.ToolName))
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
        {
            throw new AgentValidationException(
                "duplicate_tool",
                $"Tool '{duplicate.Key.ToolName}' is selected more than once.");
        }

        var installationIds = selections.Select(selection => selection.InstallationId).Distinct().ToList();

        var installations = await _context.ConnectorInstallations
            .AsNoTracking()
            .Where(installation => installation.TenantId == tenantId
                && installationIds.Contains(installation.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var registryIds = installations.Select(installation => installation.RegistryId).Distinct().ToList();

        var registries = await _context.ConnectorRegistry
            .AsNoTracking()
            .Where(registry => registryIds.Contains(registry.Id))
            .ToDictionaryAsync(registry => registry.Id, cancellationToken)
            .ConfigureAwait(false);

        var plans = new List<ToolPlan>(selections.Count);

        foreach (var selection in selections)
        {
            if (selection.InstallationId == Guid.Empty || string.IsNullOrWhiteSpace(selection.ToolName))
            {
                throw new AgentValidationException(
                    "invalid_tool",
                    "installation_id and tool_name are required for every selected tool.");
            }

            var installation = installations.FirstOrDefault(candidate => candidate.Id == selection.InstallationId)
                ?? throw new AgentValidationException(
                    "connector_installation_not_found",
                    $"Connector installation {selection.InstallationId} is not found in the tenant.");

            if (!string.Equals(installation.Status, ActiveInstallationStatus, StringComparison.Ordinal))
            {
                throw new AgentValidationException(
                    "connector_installation_disabled",
                    $"Connector installation {installation.Id} is not active.");
            }

            var registry = registries.GetValueOrDefault(installation.RegistryId)
                ?? throw new AgentValidationException(
                    "connector_registry_not_found",
                    $"Connector registry entry {installation.RegistryId} is not found.");

            var manifest = ConnectorManifestValidator.Deserialize(registry.Manifest);
            var tool = manifest.Tools.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, selection.ToolName, StringComparison.Ordinal))
                ?? throw new AgentValidationException(
                    "tool_not_in_manifest",
                    $"Tool '{selection.ToolName}' is not declared by connector '{manifest.Name}'.");

            var gateSettings = string.IsNullOrWhiteSpace(selection.GateSettings)
                ? DefaultGateSettings(tool.Policy)
                : selection.GateSettings;

            // Повышение политики отклоняется здесь (PolicyEscalationException → 400).
            var settings = ToolSettingsValidator.ParseAndValidate(
                selection.Policy,
                gateSettings,
                tool.Policy,
                tool.Name);

            plans.Add(new ToolPlan(installation.Id, tool.Name, settings, gateSettings));
        }

        var enabledTools = PlatformJson.SerializeDb(plans
            .Select(plan => new EnabledToolRef(plan.InstallationId, plan.ToolName, plan.Settings.EffectivePolicy))
            .ToList());

        return new ToolBuildResult(enabledTools, plans);
    }

    private async Task ApplyToolSettingsAsync(
        IReadOnlyList<ToolPlan> plans,
        CancellationToken cancellationToken)
    {
        if (plans.Count == 0)
        {
            return;
        }

        var installationIds = plans.Select(plan => plan.InstallationId).Distinct().ToList();

        var existing = await _context.ToolSettings
            .Where(setting => installationIds.Contains(setting.InstallationId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var plan in plans)
        {
            var setting = existing.FirstOrDefault(candidate =>
                candidate.InstallationId == plan.InstallationId
                && string.Equals(candidate.ToolName, plan.ToolName, StringComparison.Ordinal));

            if (setting is null)
            {
                setting = new ToolSetting
                {
                    InstallationId = plan.InstallationId,
                    ToolName = plan.ToolName
                };
                _context.ToolSettings.Add(setting);
            }

            setting.Enabled = true;
            setting.Policy = plan.Settings.EffectivePolicy.ToDbValue();
            setting.PolicySource = plan.Settings.PolicySource.ToDbValue();
            setting.GateSettings = plan.GateSettingsJson;
        }
    }

    private static AgentVersion CreateVersion(
        Agent agent,
        int version,
        AgentUpsertRequest request,
        string enabledToolsJson,
        Guid? actorId)
    {
        var model = new AgentModelRef(
            request.Model.Trim(),
            Endpoint: null,
            Routing: ParseRouting(request.Routing));

        var limits = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["max_turns"] = request.MaxTurns ?? new AgentLimits().MaxTurns,
            ["max_tool_calls"] = request.MaxToolCalls ?? new AgentLimits().MaxToolCalls
        };

        return new AgentVersion
        {
            AgentId = agent.Id,
            Version = version,
            Status = PublishedStatus,
            SystemPrompt = request.SystemPrompt.Trim(),
            ModelRef = PlatformJson.SerializeDb(model),
            EnabledTools = enabledToolsJson,
            Rules = "{}",
            EscalationRules = "{}",
            Limits = PlatformJson.SerializeDb(limits),
            CreatedBy = actorId
        };
    }

    private Task<AgentVersion?> FindVersionAsync(Guid agentId, int version, CancellationToken cancellationToken)
        => _context.AgentVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.AgentId == agentId && candidate.Version == version,
                cancellationToken);

    private async Task<AgentResponse> ToResponseAsync(
        Guid tenantId,
        Agent agent,
        AgentVersion version,
        CancellationToken cancellationToken)
    {
        var tools = new List<AgentToolResponse>();

        foreach (var enabled in ParseEnabledTools(version.EnabledTools))
        {
            var manifestPolicy = await FindManifestPolicyAsync(tenantId, enabled, cancellationToken)
                .ConfigureAwait(false);

            var setting = await _context.ToolSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    candidate => candidate.InstallationId == enabled.InstallationId
                        && candidate.ToolName == enabled.ToolName,
                    cancellationToken)
                .ConfigureAwait(false);

            var policySource = setting?.PolicySource
                ?? (enabled.Policy == manifestPolicy
                    ? PolicySource.Manifest.ToDbValue()
                    : PolicySource.TenantOverride.ToDbValue());

            tools.Add(new AgentToolResponse(
                enabled.InstallationId,
                enabled.ToolName,
                manifestPolicy.ToDbValue(),
                enabled.Policy.ToDbValue(),
                policySource,
                ReadGateMode(setting?.GateSettings, enabled.Policy)));
        }

        var model = ParseModel(version.ModelRef);
        var limits = ParseLimits(version.Limits);

        return new AgentResponse(
            agent.Id,
            agent.TenantId,
            agent.Name,
            version.Version,
            version.SystemPrompt,
            model.Model,
            model.Routing.ToDbValue(),
            limits.MaxTurns,
            limits.MaxToolCalls,
            tools,
            agent.CreatedAt,
            agent.UpdatedAt);
    }

    private async Task<ToolPolicy> FindManifestPolicyAsync(
        Guid tenantId,
        EnabledToolRef enabled,
        CancellationToken cancellationToken)
    {
        var installation = await _context.ConnectorInstallations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.TenantId == tenantId && candidate.Id == enabled.InstallationId,
                cancellationToken)
            .ConfigureAwait(false);

        if (installation is null)
        {
            return enabled.Policy;
        }

        var manifestJson = await _context.ConnectorRegistry
            .AsNoTracking()
            .Where(registry => registry.Id == installation.RegistryId)
            .Select(registry => registry.Manifest)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return enabled.Policy;
        }

        try
        {
            var manifest = ConnectorManifestValidator.Deserialize(manifestJson);
            var tool = manifest.Tools.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, enabled.ToolName, StringComparison.Ordinal));

            return tool?.Policy ?? enabled.Policy;
        }
        catch (JsonValidationException)
        {
            return enabled.Policy;
        }
    }

    private static List<EnabledToolRef> ParseEnabledTools(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return PlatformJson.DeserializeDb<List<EnabledToolRef>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static AgentModelRef ParseModel(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AgentModelRef("platform-default");
        }

        try
        {
            return PlatformJson.DeserializeDb<AgentModelRef>(json)
                ?? new AgentModelRef("platform-default");
        }
        catch (JsonException)
        {
            return new AgentModelRef("platform-default");
        }
    }

    private static AgentLimits ParseLimits(string? json)
    {
        var defaults = new AgentLimits();

        if (string.IsNullOrWhiteSpace(json))
        {
            return defaults;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            return new AgentLimits(
                ReadInt(root, "max_turns", defaults.MaxTurns),
                ReadInt(root, "max_tool_calls", defaults.MaxToolCalls));
        }
        catch (JsonException)
        {
            return defaults;
        }
    }

    private static int ReadInt(JsonElement root, string propertyName, int fallback)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(propertyName, out var value)
            && value.TryGetInt32(out var result)
                ? result
                : fallback;

    private static string ReadGateMode(string? gateSettingsJson, ToolPolicy effectivePolicy)
    {
        if (!string.IsNullOrWhiteSpace(gateSettingsJson))
        {
            try
            {
                using var document = JsonDocument.Parse(gateSettingsJson);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("mode", out var mode)
                    && mode.ValueKind == JsonValueKind.String)
                {
                    return mode.GetString() ?? GateMode.Always.ToDbValue();
                }
            }
            catch (JsonException)
            {
                // Некорректные настройки трактуются как самый строгий режим.
                return GateMode.Always.ToDbValue();
            }
        }

        return effectivePolicy == ToolPolicy.Read
            ? GateMode.Never.ToDbValue()
            : GateMode.Always.ToDbValue();
    }

    private static string DefaultGateSettings(ToolPolicy manifestPolicy)
        => manifestPolicy == ToolPolicy.Read
            ? """{"mode":"never"}"""
            : """{"mode":"always"}""";

    private static ModelRouting ParseRouting(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return ModelRouting.Simple;
        }

        return EnumTokens.TryParseDbValue<ModelRouting>(token, out var routing)
            ? routing
            : throw new AgentValidationException(
                "invalid_routing",
                $"Unknown routing '{token}'. Allowed: simple, complex.");
    }

    private async Task<Guid?> ResolveActorAsync(
        Guid tenantId,
        Guid? actorId,
        CancellationToken cancellationToken)
    {
        if (actorId is not { } candidate || candidate == Guid.Empty)
        {
            return null;
        }

        var exists = await _context.Users
            .AsNoTracking()
            .AnyAsync(
                user => user.TenantId == tenantId && user.Id == candidate,
                cancellationToken)
            .ConfigureAwait(false);

        return exists ? candidate : null;
    }

    private static void ValidateRequest(AgentUpsertRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200)
        {
            throw new AgentValidationException(
                "invalid_agent_name",
                "name is required and must be 1..200 characters.");
        }

        if (string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            throw new AgentValidationException("invalid_system_prompt", "systemPrompt is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Model))
        {
            throw new AgentValidationException("invalid_model", "model is required.");
        }

        ValidateLimit(request.MaxTurns, "maxTurns");
        ValidateLimit(request.MaxToolCalls, "maxToolCalls");
    }

    private static void ValidateLimit(int? value, string field)
    {
        if (value is { } limit && (limit <= 0 || limit > 1000))
        {
            throw new AgentValidationException(
                "invalid_limit",
                $"{field} must be in 1..1000, got {limit}.");
        }
    }

    private void RequireTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("tenant_id is required", nameof(tenantId));
        }

        _tenantContext.SetTenant(tenantId);
    }

    private sealed record ToolPlan(
        Guid InstallationId,
        string ToolName,
        ResolvedToolSettings Settings,
        string GateSettingsJson);

    private sealed record ToolBuildResult(string EnabledToolsJson, IReadOnlyList<ToolPlan> Tools);
}
