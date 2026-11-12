namespace AgentPlatform.Controller.Contracts;

/// <summary>
/// Выбор тула для версии агента. Политика тенанта может только понижать
/// манифестную (read &lt; write &lt; destructive); повышение отклоняется API.
/// </summary>
/// <param name="InstallationId">Установка коннектора (connector_installations.id).</param>
/// <param name="ToolName">Имя тула из манифеста.</param>
/// <param name="Policy">Политика тенанта (null — манифестная).</param>
/// <param name="GateSettings">jsonb gate_settings; null — режим по умолчанию (read → never, остальные → always).</param>
public sealed record AgentToolSelectionRequest(
    Guid InstallationId,
    string ToolName,
    string? Policy = null,
    string? GateSettings = null);

/// <summary>
/// Создание/изменение агента. Изменение публикует новую неизменяемую версию
/// (version pinning не ломается): старые сессии продолжают работать на своей версии.
/// </summary>
/// <param name="Name">Имя агента (уникально в тенанте).</param>
/// <param name="SystemPrompt">Системный промпт версии.</param>
/// <param name="Model">Модель (agent_versions.model_ref.model).</param>
/// <param name="Routing">simple | complex (по умолчанию simple).</param>
/// <param name="Tools">Включаемые тулы с политиками.</param>
/// <param name="MaxTurns">Лимит ходов LLM.</param>
/// <param name="MaxToolCalls">Лимит вызовов тулов.</param>
public sealed record AgentUpsertRequest(
    string Name,
    string SystemPrompt,
    string Model,
    string? Routing = null,
    IReadOnlyList<AgentToolSelectionRequest>? Tools = null,
    int? MaxTurns = null,
    int? MaxToolCalls = null,
    Guid? ActorId = null);

/// <summary>Тул агента в ответе API: манифестная политика, действующая политика и режим гейта.</summary>
public sealed record AgentToolResponse(
    Guid InstallationId,
    string ToolName,
    string ManifestPolicy,
    string EffectivePolicy,
    string PolicySource,
    string GateMode);

/// <summary>Агент с параметрами текущей версии.</summary>
public sealed record AgentResponse(
    Guid Id,
    Guid TenantId,
    string Name,
    int CurrentVersion,
    string SystemPrompt,
    string Model,
    string Routing,
    int MaxTurns,
    int MaxToolCalls,
    IReadOnlyList<AgentToolResponse> Tools,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
