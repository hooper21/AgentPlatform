using System.Text.Json;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Json;
using AgentPlatform.Core.Validation;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Infrastructure.Repositories;

/// <summary>
/// Загрузка конфигурации закреплённой версии агента из БД (порт <see cref="IAgentConfigStore"/>):
/// agent_versions (промпт, модель, лимиты, включённые тулы) + установленные коннекторы
/// (манифест тула) + tool_settings (политика и режим гейта тенанта).
/// <para>
/// Тулы, чей коннектор отключён/удалён или выключен в tool_settings, в конфигурацию
/// не попадают — модель их не увидит. Повышение политики отклоняется
/// <see cref="ToolSettingsValidator"/> уже на этом шаге (fail-closed).
/// </para>
/// </summary>
public sealed class EfAgentConfigStore(AgentPlatformDbContext context) : IAgentConfigStore
{
    private const string ActiveStatus = "active";
    private const string ManifestPolicySource = "manifest";

    private readonly AgentPlatformDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public async Task<AgentRuntimeConfig?> GetAsync(
        Guid tenantId,
        Guid agentId,
        int agentVersion,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("tenant_id is required", nameof(tenantId));
        }

        if (agentId == Guid.Empty)
        {
            throw new ArgumentException("agent_id is required", nameof(agentId));
        }

        var agentExists = await _context.Agents
            .AsNoTracking()
            .AnyAsync(agent => agent.Id == agentId && agent.TenantId == tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (!agentExists)
        {
            return null;
        }

        var version = await _context.AgentVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.AgentId == agentId && candidate.Version == agentVersion,
                cancellationToken)
            .ConfigureAwait(false);

        if (version is null)
        {
            return null;
        }

        var tools = await LoadToolsAsync(tenantId, version.EnabledTools, cancellationToken).ConfigureAwait(false);

        return new AgentRuntimeConfig
        {
            TenantId = tenantId,
            AgentId = agentId,
            Version = version.Version,
            SystemPrompt = version.SystemPrompt,
            Model = ParseModel(version.ModelRef),
            Tools = tools,
            Limits = ParseLimits(version.Limits),
            EscalationRules = ParseEscalationRules(version.EscalationRules)
        };
    }

    private async Task<IReadOnlyList<AgentToolConfig>> LoadToolsAsync(
        Guid tenantId,
        string enabledToolsJson,
        CancellationToken cancellationToken)
    {
        var enabledTools = ParseEnabledTools(enabledToolsJson);
        var result = new List<AgentToolConfig>();

        foreach (var enabledTool in enabledTools)
        {
            var installation = await _context.ConnectorInstallations
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    candidate => candidate.Id == enabledTool.InstallationId
                        && candidate.TenantId == tenantId
                        && candidate.Status == ActiveStatus,
                    cancellationToken)
                .ConfigureAwait(false);

            if (installation is null)
            {
                continue;
            }

            var registry = await _context.ConnectorRegistry
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == installation.RegistryId, cancellationToken)
                .ConfigureAwait(false);

            if (registry is null)
            {
                continue;
            }

            var manifest = ParseManifest(registry);
            var tool = manifest?.Tools.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, enabledTool.ToolName, StringComparison.Ordinal));

            if (tool is null)
            {
                continue;
            }

            var setting = await _context.ToolSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    candidate => candidate.InstallationId == installation.Id
                        && candidate.ToolName == tool.Name,
                    cancellationToken)
                .ConfigureAwait(false);

            if (setting is { Enabled: false })
            {
                continue;
            }

            var policyToken = setting is null || setting.PolicySource == ManifestPolicySource
                ? null
                : setting.Policy;

            var toolContext = ToolSettingsValidator.CreateToolContext(
                installation.Id,
                tool.Name,
                tool.Policy,
                policyToken,
                setting?.GateSettings);

            result.Add(new AgentToolConfig(
                setting?.DescriptionOverride ?? tool.Description,
                tool.InputSchema,
                toolContext));
        }

        return result;
    }

    private static AgentModelRef ParseModel(string? json)
    {
        AgentModelRef? model;

        try
        {
            model = PlatformJson.DeserializeDb<AgentModelRef>(json ?? string.Empty);
        }
        catch (JsonException exception)
        {
            throw new JsonValidationException("agent_versions.model_ref", "must be a valid JSON object", exception);
        }

        if (model is null || string.IsNullOrWhiteSpace(model.Model))
        {
            throw new JsonValidationException("agent_versions.model_ref", "'model' is required");
        }

        return model;
    }

    private static ConnectorManifest? ParseManifest(ConnectorRegistryEntry registry)
    {
        try
        {
            return PlatformJson.DeserializeDb<ConnectorManifest>(registry.Manifest);
        }
        catch (JsonException exception)
        {
            throw new JsonValidationException(
                "connector_registry.manifest",
                $"connector '{registry.Name}' manifest must be a valid JSON document",
                exception);
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
        catch (JsonException exception)
        {
            throw new JsonValidationException(
                "agent_versions.enabled_tools",
                "must be a valid JSON array",
                exception);
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
                ReadInt(root, "max_tool_calls", defaults.MaxToolCalls),
                ReadLong(root, "max_tokens", defaults.MaxTokens));
        }
        catch (JsonException exception)
        {
            throw new JsonValidationException("agent_versions.limits", "must be a valid JSON object", exception);
        }
    }

    private static AgentEscalationRules ParseEscalationRules(string? json)
    {
        var defaults = new AgentEscalationRules();

        if (string.IsNullOrWhiteSpace(json))
        {
            return defaults;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return new AgentEscalationRules(
                ReadInt(document.RootElement, "failed_turns", defaults.FailedTurnsThreshold));
        }
        catch (JsonException exception)
        {
            throw new JsonValidationException(
                "agent_versions.escalation_rules",
                "must be a valid JSON object",
                exception);
        }
    }

    private static int ReadInt(JsonElement root, string propertyName, int fallback)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(propertyName, out var value)
            && value.TryGetInt32(out var result)
                ? result
                : fallback;

    private static long ReadLong(JsonElement root, string propertyName, long fallback)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(propertyName, out var value)
            && value.TryGetInt64(out var result)
                ? result
                : fallback;
}

