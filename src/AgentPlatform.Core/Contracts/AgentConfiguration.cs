using AgentPlatform.Core.Enums;

namespace AgentPlatform.Core.Contracts;

/// <summary>
/// Ссылка на модель агента (agent_versions.model_ref):
/// <c>{endpoint?, model, routing}</c>.
/// </summary>
/// <param name="Model">Имя модели у провайдера.</param>
/// <param name="Endpoint">Собственный endpoint тенанта (для llm_config.mode = own).</param>
/// <param name="Routing">Маршрутизация: simple | complex.</param>
public sealed record AgentModelRef(
    string Model,
    string? Endpoint = null,
    ModelRouting Routing = ModelRouting.Simple);

/// <summary>
/// LLM-конфигурация тенанта (tenants.llm_config):
/// <c>{mode, endpoint?, api_key_ref?, model}</c>.
/// Секрет модели хранится только по ссылке (api_key_ref), не в конфиге.
/// </summary>
/// <param name="Mode">platform — платформенная модель, own — собственный endpoint тенанта.</param>
/// <param name="Model">Имя модели.</param>
/// <param name="Endpoint">Endpoint собственной модели.</param>
/// <param name="ApiKeyRef">Ссылка на секрет в Secret Service.</param>
public sealed record TenantLlmConfig(
    LlmMode Mode,
    string Model,
    string? Endpoint = null,
    string? ApiKeyRef = null);

/// <summary>
/// Правила агента (agent_versions.rules). Валидация обязательна (REQUIRED):
/// схема проверяется кодом (<see cref="Validation.AgentRulesValidator"/>),
/// CHECK-ограничения в БД в v1 не используются.
/// </summary>
/// <param name="Qualification">Правила квалификации лида/запроса.</param>
/// <param name="QualificationQuestions">Вопросы квалификации.</param>
/// <param name="Restrictions">Ограничения (что агенту делать нельзя).</param>
/// <param name="AskBeforeWrite">Требовать подтверждения перед write-действием.</param>
/// <param name="Notes">Свободные заметки.</param>
public sealed record AgentRules(
    string? Qualification = null,
    IReadOnlyList<string>? QualificationQuestions = null,
    IReadOnlyList<string>? Restrictions = null,
    bool? AskBeforeWrite = null,
    string? Notes = null);

/// <summary>
/// Настройки гейта тула (tool_settings.gate_settings):
/// <c>{mode, predicate?}</c>.
/// </summary>
/// <param name="Mode">always | conditional | never.</param>
/// <param name="Predicate">Условие для режима conditional (серверное, не промпт).</param>
public sealed record ToolGateSettings(GateMode Mode, string? Predicate = null);
