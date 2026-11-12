using System.Text.Json;
using AgentPlatform.Controller.Contracts;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Json;
using AgentPlatform.Core.Policy;
using AgentPlatform.Core.Validation;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Persistence.Entities;
using AgentPlatform.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Controller.Connectors;

/// <summary>Ошибка валидации установки/настроек коннектора.</summary>
public sealed class ConnectorValidationException(string code, string message) : Exception(message)
{
    /// <summary>Машиночитаемый код ошибки для API/UI.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// Реестр и установки коннекторов тенанта. Credentials шифруются Secret Service
/// (AES-256, KMS-заглушка) и хранятся только по ссылке; API значения не возвращает.
/// При установке создаются tool_settings по умолчанию (read → never, остальные → always).
/// </summary>
public sealed class ConnectorService(
    AgentPlatformDbContext context,
    TenantContext tenantContext,
    ISecretService secrets)
{
    private const string PublishedStatus = "published";
    private const string ActiveStatus = "active";
    private const string NoCredentialsRef = "none";
    private const string SecretsPrefix = "secret://local/";

    private readonly AgentPlatformDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    private readonly TenantContext _tenantContext =
        tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));

    private readonly ISecretService _secrets =
        secrets ?? throw new ArgumentNullException(nameof(secrets));

    /// <summary>Каталог опубликованных коннекторов платформы (глобальная таблица).</summary>
    public async Task<IReadOnlyList<ConnectorCatalogItemResponse>> ListCatalogAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);

        var entries = await _context.ConnectorRegistry
            .AsNoTracking()
            .Where(entry => entry.Status == PublishedStatus)
            .OrderBy(entry => entry.Name)
            .ThenBy(entry => entry.Version)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var result = new List<ConnectorCatalogItemResponse>(entries.Count);

        foreach (var entry in entries)
        {
            try
            {
                var manifest = ConnectorManifestValidator.Deserialize(entry.Manifest);

                result.Add(new ConnectorCatalogItemResponse(
                    entry.Id,
                    manifest.Name,
                    manifest.Version,
                    manifest.Description,
                    manifest.Auth.Type.ToDbValue(),
                    manifest.Auth.Fields?.ToList() ?? [],
                    manifest.Tools.Select(tool => new ConnectorToolCatalogResponse(
                        tool.Name,
                        tool.Description,
                        tool.Policy.ToDbValue(),
                        tool.InputSchema)).ToList()));
            }
            catch (JsonValidationException)
            {
                // Битый манифест не должен ронять каталог: он просто не устанавливается.
                continue;
            }
        }

        return result;
    }

    /// <summary>Установленные коннекторы тенанта.</summary>
    public async Task<IReadOnlyList<ConnectorInstallationResponse>> ListInstallationsAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);

        var installations = await _context.ConnectorInstallations
            .AsNoTracking()
            .Where(installation => installation.TenantId == tenantId)
            .OrderBy(installation => installation.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var registryIds = installations.Select(installation => installation.RegistryId).Distinct().ToList();

        var registries = await _context.ConnectorRegistry
            .AsNoTracking()
            .Where(entry => registryIds.Contains(entry.Id))
            .ToDictionaryAsync(entry => entry.Id, cancellationToken)
            .ConfigureAwait(false);

        var installationIds = installations.Select(installation => installation.Id).ToList();

        var toolCounts = await _context.ToolSettings
            .AsNoTracking()
            .Where(setting => installationIds.Contains(setting.InstallationId))
            .GroupBy(setting => setting.InstallationId)
            .Select(group => new { InstallationId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.InstallationId, item => item.Count, cancellationToken)
            .ConfigureAwait(false);

        return installations
            .Select(installation =>
            {
                registries.TryGetValue(installation.RegistryId, out var registry);

                return new ConnectorInstallationResponse(
                    installation.Id,
                    installation.TenantId,
                    installation.RegistryId,
                    registry?.Name ?? "unknown",
                    registry?.Version ?? "unknown",
                    installation.AuthType,
                    installation.Status,
                    installation.ExternalUrl,
                    toolCounts.GetValueOrDefault(installation.Id),
                    installation.CreatedAt,
                    installation.UpdatedAt);
            })
            .ToList();
    }

    /// <summary>Устанавливает коннектор и создаёт tool_settings по умолчанию.</summary>
    public async Task<ConnectorInstallationResponse> InstallAsync(
        Guid tenantId,
        ConnectorInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireTenant(tenantId);

        if (request.RegistryId == Guid.Empty)
        {
            throw new ConnectorValidationException("registry_id_required", "registryId is required.");
        }

        var registry = await _context.ConnectorRegistry
            .AsNoTracking()
            .FirstOrDefaultAsync(
                entry => entry.Id == request.RegistryId && entry.Status == PublishedStatus,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ConnectorValidationException(
                "connector_not_found",
                $"Published connector {request.RegistryId} is not found.");

        if (await _context.ConnectorInstallations.AnyAsync(
                installation => installation.TenantId == tenantId
                    && installation.RegistryId == request.RegistryId,
                cancellationToken).ConfigureAwait(false))
        {
            throw new ConnectorValidationException(
                "connector_already_installed",
                $"Connector '{registry.Name}' is already installed in the tenant.");
        }

        var manifest = ConnectorManifestValidator.Deserialize(registry.Manifest);
        var credentialsRef = await StoreCredentialsAsync(tenantId, manifest, request.Credentials, cancellationToken)
            .ConfigureAwait(false);
        var externalUrl = ValidateExternalUrl(manifest, request.ExternalUrl);

        var installation = new ConnectorInstallation
        {
            TenantId = tenantId,
            RegistryId = registry.Id,
            CredentialsRef = credentialsRef,
            AuthType = manifest.Auth.Type.ToDbValue(),
            ExternalUrl = externalUrl,
            Status = ActiveStatus,
            HealthMeta = "{}"
        };

        _context.ConnectorInstallations.Add(installation);
        ApplyDefaultToolSettings(installation.Id, manifest);

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new ConnectorInstallationResponse(
            installation.Id,
            tenantId,
            registry.Id,
            registry.Name,
            registry.Version,
            installation.AuthType,
            installation.Status,
            installation.ExternalUrl,
            manifest.Tools.Count,
            installation.CreatedAt,
            installation.UpdatedAt);
    }

    /// <summary>Изменяет статус/URL/credentials установки.</summary>
    public async Task<ConnectorInstallationResponse?> UpdateAsync(
        Guid tenantId,
        Guid installationId,
        ConnectorInstallationUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireTenant(tenantId);

        var installation = await _context.ConnectorInstallations
            .FirstOrDefaultAsync(
                candidate => candidate.TenantId == tenantId && candidate.Id == installationId,
                cancellationToken)
            .ConfigureAwait(false);

        if (installation is null)
        {
            return null;
        }

        var registry = await _context.ConnectorRegistry
            .AsNoTracking()
            .FirstOrDefaultAsync(entry => entry.Id == installation.RegistryId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ConnectorValidationException(
                "connector_not_found",
                $"Connector registry entry {installation.RegistryId} is not found.");

        var manifest = ConnectorManifestValidator.Deserialize(registry.Manifest);

        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            if (!EnumTokens.TryParseDbValue<ConnectorInstallationStatus>(request.Status, out var status))
            {
                throw new ConnectorValidationException(
                    "invalid_connector_status",
                    "status must be one of: active, error, disabled.");
            }

            installation.Status = status.ToDbValue();
        }

        if (request.ExternalUrl is not null)
        {
            installation.ExternalUrl = ValidateExternalUrl(manifest, request.ExternalUrl);
        }

        var previousCredentialsRef = installation.CredentialsRef;
        var credentialsRotated = request.Credentials is { Count: > 0 };

        if (credentialsRotated)
        {
            installation.CredentialsRef = await StoreCredentialsAsync(
                    tenantId,
                    manifest,
                    request.Credentials,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (credentialsRotated
            && !string.Equals(previousCredentialsRef, installation.CredentialsRef, StringComparison.Ordinal)
            && previousCredentialsRef.StartsWith(SecretsPrefix, StringComparison.Ordinal))
        {
            await _secrets.DeleteAsync(tenantId, previousCredentialsRef, cancellationToken)
                .ConfigureAwait(false);
        }

        var toolCount = await _context.ToolSettings
            .AsNoTracking()
            .CountAsync(setting => setting.InstallationId == installation.Id, cancellationToken)
            .ConfigureAwait(false);

        return new ConnectorInstallationResponse(
            installation.Id,
            tenantId,
            registry.Id,
            registry.Name,
            registry.Version,
            installation.AuthType,
            installation.Status,
            installation.ExternalUrl,
            toolCount,
            installation.CreatedAt,
            installation.UpdatedAt);
    }

    /// <summary>
    /// Удаляет установку, если её тулы не включены в версии агентов.
    /// Секрет установки удаляется вместе с ней.
    /// </summary>
    public async Task<bool> DeleteAsync(
        Guid tenantId,
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);

        var installation = await _context.ConnectorInstallations
            .FirstOrDefaultAsync(
                candidate => candidate.TenantId == tenantId && candidate.Id == installationId,
                cancellationToken)
            .ConfigureAwait(false);

        if (installation is null)
        {
            return false;
        }

        if (await IsInstallationUsedAsync(tenantId, installationId, cancellationToken).ConfigureAwait(false))
        {
            throw new ConnectorValidationException(
                "connector_in_use",
                "Connector tools are enabled in an agent version; publish a version without them first.");
        }

        var settings = await _context.ToolSettings
            .Where(setting => setting.InstallationId == installationId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        _context.ToolSettings.RemoveRange(settings);
        _context.ConnectorInstallations.Remove(installation);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (installation.CredentialsRef.StartsWith(SecretsPrefix, StringComparison.Ordinal))
        {
            await _secrets.DeleteAsync(tenantId, installation.CredentialsRef, cancellationToken)
                .ConfigureAwait(false);
        }

        return true;
    }

    private async Task<bool> IsInstallationUsedAsync(
        Guid tenantId,
        Guid installationId,
        CancellationToken cancellationToken)
    {
        var agentIds = await _context.Agents
            .AsNoTracking()
            .Where(agent => agent.TenantId == tenantId)
            .Select(agent => agent.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (agentIds.Count == 0)
        {
            return false;
        }

        var versions = await _context.AgentVersions
            .AsNoTracking()
            .Where(version => agentIds.Contains(version.AgentId))
            .Select(version => version.EnabledTools)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var json in versions)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            try
            {
                var enabledTools = PlatformJson.DeserializeDb<List<EnabledToolRef>>(json);
                if (enabledTools is not null
                    && enabledTools.Any(tool => tool.InstallationId == installationId))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // Битые enabled_tools не должны блокировать удаление навсегда.
                continue;
            }
        }

        return false;
    }

    /// <summary>Настройки всех тулов установки (null — установка не найдена).</summary>
    public async Task<IReadOnlyList<ToolSettingResponse>?> ListToolSettingsAsync(
        Guid tenantId,
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);

        var installationContext = await GetInstallationContextAsync(tenantId, installationId, cancellationToken)
            .ConfigureAwait(false);

        if (installationContext is null)
        {
            return null;
        }

        var settings = await _context.ToolSettings
            .AsNoTracking()
            .Where(setting => setting.InstallationId == installationId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var result = new List<ToolSettingResponse>(installationContext.Manifest.Tools.Count);

        foreach (var tool in installationContext.Manifest.Tools)
        {
            var setting = settings.FirstOrDefault(candidate =>
                string.Equals(candidate.ToolName, tool.Name, StringComparison.Ordinal));

            var effectivePolicy = setting is not null
                && EnumTokens.TryParseDbValue<ToolPolicy>(setting.Policy, out var parsedPolicy)
                    ? parsedPolicy
                    : tool.Policy;

            result.Add(new ToolSettingResponse(
                setting?.Id ?? Guid.Empty,
                installationId,
                tool.Name,
                setting?.Enabled ?? true,
                tool.Policy.ToDbValue(),
                effectivePolicy.ToDbValue(),
                setting?.PolicySource ?? PolicySource.Manifest.ToDbValue(),
                ReadGateMode(setting?.GateSettings, effectivePolicy),
                setting?.GateSettings ?? DefaultGateSettings(tool.Policy),
                setting?.DescriptionOverride,
                setting?.CreatedAt ?? default,
                setting?.UpdatedAt ?? default));
        }

        return result;
    }

    /// <summary>Создаёт/обновляет tool_settings тула (повышение политики отклоняется).</summary>
    public async Task<ToolSettingResponse?> UpsertToolSettingAsync(
        Guid tenantId,
        Guid installationId,
        string toolName,
        ToolSettingUpsertRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireTenant(tenantId);

        if (string.IsNullOrWhiteSpace(toolName))
        {
            throw new ConnectorValidationException("tool_name_required", "tool_name is required.");
        }

        var installationContext = await GetInstallationContextAsync(tenantId, installationId, cancellationToken)
            .ConfigureAwait(false);

        if (installationContext is null)
        {
            return null;
        }

        var tool = installationContext.Manifest.Tools.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, toolName, StringComparison.Ordinal))
            ?? throw new ConnectorValidationException(
                "tool_not_in_manifest",
                $"Tool '{toolName}' is not declared by connector '{installationContext.Manifest.Name}'.");

        var setting = await _context.ToolSettings
            .FirstOrDefaultAsync(
                candidate => candidate.InstallationId == installationId
                    && candidate.ToolName == toolName,
                cancellationToken)
            .ConfigureAwait(false);

        var gateSettings = string.IsNullOrWhiteSpace(request.GateSettings)
            ? setting?.GateSettings ?? DefaultGateSettings(tool.Policy)
            : request.GateSettings;

        // Единая проверка политики с Runtime: повышение → PolicyEscalationException.
        var resolved = ToolSettingsValidator.ParseAndValidate(
            request.Policy,
            gateSettings,
            tool.Policy,
            tool.Name);

        if (setting is null)
        {
            setting = new ToolSetting
            {
                InstallationId = installationId,
                ToolName = tool.Name
            };
            _context.ToolSettings.Add(setting);
        }

        setting.Enabled = request.Enabled;
        setting.Policy = resolved.EffectivePolicy.ToDbValue();
        setting.PolicySource = resolved.PolicySource.ToDbValue();
        setting.GateSettings = gateSettings;

        if (request.DescriptionOverride is not null)
        {
            setting.DescriptionOverride = request.DescriptionOverride;
        }

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new ToolSettingResponse(
            setting.Id,
            installationId,
            tool.Name,
            setting.Enabled,
            tool.Policy.ToDbValue(),
            resolved.EffectivePolicy.ToDbValue(),
            setting.PolicySource,
            resolved.GateMode.ToDbValue(),
            setting.GateSettings,
            setting.DescriptionOverride,
            setting.CreatedAt,
            setting.UpdatedAt);
    }

    /// <summary>Удаляет настройку тула (действуют манифестные значения по умолчанию).</summary>
    public async Task<bool> DeleteToolSettingAsync(
        Guid tenantId,
        Guid installationId,
        string toolName,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);

        var installationExists = await _context.ConnectorInstallations
            .AsNoTracking()
            .AnyAsync(
                installation => installation.TenantId == tenantId && installation.Id == installationId,
                cancellationToken)
            .ConfigureAwait(false);

        if (!installationExists)
        {
            return false;
        }

        var setting = await _context.ToolSettings
            .FirstOrDefaultAsync(
                candidate => candidate.InstallationId == installationId
                    && candidate.ToolName == toolName,
                cancellationToken)
            .ConfigureAwait(false);

        if (setting is null)
        {
            return false;
        }

        _context.ToolSettings.Remove(setting);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    private async Task<InstallationContext?> GetInstallationContextAsync(
        Guid tenantId,
        Guid installationId,
        CancellationToken cancellationToken)
    {
        var installation = await _context.ConnectorInstallations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.TenantId == tenantId && candidate.Id == installationId,
                cancellationToken)
            .ConfigureAwait(false);

        if (installation is null)
        {
            return null;
        }

        var manifestJson = await _context.ConnectorRegistry
            .AsNoTracking()
            .Where(entry => entry.Id == installation.RegistryId)
            .Select(entry => entry.Manifest)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ConnectorValidationException(
                "connector_manifest_not_found",
                $"Connector manifest for installation {installationId} is not found.");

        return new InstallationContext(
            installation,
            ConnectorManifestValidator.Deserialize(manifestJson));
    }

    /// <summary>
    /// Шифрует credentials через Secret Service. Набор полей задаёт манифест:
    /// незнакомые поля отклоняются, отсутствующие обязательные — ошибка.
    /// </summary>
    private async Task<string> StoreCredentialsAsync(
        Guid tenantId,
        ConnectorManifest manifest,
        IReadOnlyDictionary<string, string>? credentials,
        CancellationToken cancellationToken)
    {
        var fields = manifest.Auth.Fields ?? [];

        if (fields.Count == 0)
        {
            if (credentials is { Count: > 0 })
            {
                throw new ConnectorValidationException(
                    "credentials_not_supported",
                    $"Connector '{manifest.Name}' does not declare credentials.");
            }

            return NoCredentialsRef;
        }

        if (credentials is null || credentials.Count == 0)
        {
            throw new ConnectorValidationException(
                "credentials_required",
                $"Connector '{manifest.Name}' requires credentials: {string.Join(", ", fields)}.");
        }

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (field, value) in credentials)
        {
            if (!fields.Contains(field, StringComparer.Ordinal))
            {
                throw new ConnectorValidationException(
                    "unknown_credential_field",
                    $"Credential field '{field}' is not declared by the connector manifest.");
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ConnectorValidationException(
                    "empty_credential_field",
                    $"Credential field '{field}' must not be empty.");
            }

            normalized[field] = value.Trim();
        }

        var missing = fields.Where(field => !normalized.ContainsKey(field)).ToList();
        if (missing.Count > 0)
        {
            throw new ConnectorValidationException(
                "credentials_incomplete",
                $"Missing credential fields: {string.Join(", ", missing)}.");
        }

        return await _secrets.StoreAsync(tenantId, normalized, cancellationToken).ConfigureAwait(false);
    }

    private static string? ValidateExternalUrl(ConnectorManifest manifest, string? externalUrl)
    {
        if (string.IsNullOrWhiteSpace(externalUrl))
        {
            // Внутренний EchoTools хостится платформой и URL не требует.
            if (manifest.Auth.Type == ConnectorAuthType.ExternalMcp
                && !string.Equals(manifest.Name, "echo", StringComparison.Ordinal))
            {
                throw new ConnectorValidationException(
                    "external_url_required",
                    "externalUrl is required for an external MCP connector.");
            }

            return null;
        }

        var trimmed = externalUrl.Trim();

        if (trimmed.Length > 500)
        {
            throw new ConnectorValidationException(
                "external_url_invalid",
                "externalUrl must be at most 500 characters.");
        }

        if (manifest.Auth.Type != ConnectorAuthType.ExternalMcp)
        {
            throw new ConnectorValidationException(
                "external_url_not_supported",
                "externalUrl is supported for external_mcp connectors only.");
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ConnectorValidationException(
                "external_url_invalid",
                "externalUrl must be an absolute http(s) URL.");
        }

        return trimmed;
    }

    private void ApplyDefaultToolSettings(Guid installationId, ConnectorManifest manifest)
    {
        foreach (var tool in manifest.Tools)
        {
            _context.ToolSettings.Add(new ToolSetting
            {
                InstallationId = installationId,
                ToolName = tool.Name,
                Enabled = true,
                Policy = tool.Policy.ToDbValue(),
                PolicySource = PolicySource.Manifest.ToDbValue(),
                GateSettings = DefaultGateSettings(tool.Policy)
            });
        }
    }

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

    private void RequireTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("tenant_id is required", nameof(tenantId));
        }

        _tenantContext.SetTenant(tenantId);
    }

    private sealed record InstallationContext(
        ConnectorInstallation Installation,
        ConnectorManifest Manifest);
}
