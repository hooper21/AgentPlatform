namespace AgentPlatform.Controller.Contracts;

/// <summary>Тул в каталоге коннекторов (для выбора в агенте).</summary>
public sealed record ConnectorToolCatalogResponse(
    string Name,
    string Description,
    string Policy,
    string InputSchema);

/// <summary>Запись каталога коннекторов (connector_registry, глобальная таблица).</summary>
public sealed record ConnectorCatalogItemResponse(
    Guid Id,
    string Name,
    string Version,
    string Description,
    string AuthType,
    IReadOnlyList<string> AuthFields,
    IReadOnlyList<ConnectorToolCatalogResponse> Tools);

/// <summary>
/// Установка коннектора в тенанте. Credentials шифруются AES-256 (KMS-заглушка)
/// и сохраняются по ссылке; в API значения не возвращаются.
/// </summary>
public sealed record ConnectorInstallRequest(
    Guid RegistryId,
    IReadOnlyDictionary<string, string>? Credentials = null,
    string? ExternalUrl = null);

/// <summary>Изменение установки: статус, URL внешнего MCP, ротация credentials.</summary>
public sealed record ConnectorInstallationUpdateRequest(
    string? Status = null,
    string? ExternalUrl = null,
    IReadOnlyDictionary<string, string>? Credentials = null);

/// <summary>Установленный коннектор тенанта.</summary>
public sealed record ConnectorInstallationResponse(
    Guid Id,
    Guid TenantId,
    Guid RegistryId,
    string ConnectorName,
    string ConnectorVersion,
    string AuthType,
    string Status,
    string? ExternalUrl,
    int ToolCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Изменение tool_settings тула установки.</summary>
public sealed record ToolSettingUpsertRequest(
    string? Policy = null,
    string? GateSettings = null,
    bool Enabled = true,
    string? DescriptionOverride = null);

/// <summary>Настройка тула в ответе API.</summary>
public sealed record ToolSettingResponse(
    Guid Id,
    Guid InstallationId,
    string ToolName,
    bool Enabled,
    string ManifestPolicy,
    string EffectivePolicy,
    string PolicySource,
    string GateMode,
    string GateSettings,
    string? DescriptionOverride,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
