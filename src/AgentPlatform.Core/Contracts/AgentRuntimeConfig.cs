using AgentPlatform.Core.Enums;

namespace AgentPlatform.Core.Contracts;

/// <summary>
/// Собранная конфигурация версии агента для Runtime: промпт, модель, активные тулы
/// подключённых коннекторов и лимиты. Загружается из agents/agent_versions +
/// connector_installations/connector_registry/tool_settings.
/// <para>
/// Версия фиксируется на старте сессии (version pinning) и не меняется при resume:
/// Runtime запрашивает именно <see cref="Version"/>, а не agents.current_version.
/// </para>
/// </summary>
public sealed record AgentRuntimeConfig
{
    /// <summary>Тенант агента.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>Агент.</summary>
    public required Guid AgentId { get; init; }

    /// <summary>Версия агента (agent_versions.version) — закреплена за сессией.</summary>
    public required int Version { get; init; }

    /// <summary>Системный промпт версии (agent_versions.system_prompt).</summary>
    public string SystemPrompt { get; init; } = string.Empty;

    /// <summary>Ссылка на модель (agent_versions.model_ref).</summary>
    public required AgentModelRef Model { get; init; }

    /// <summary>Активные тулы подключённых коннекторов, разрешённые в этой версии.</summary>
    public IReadOnlyList<AgentToolConfig> Tools { get; init; } = [];

    /// <summary>Лимиты версии (agent_versions.limits).</summary>
    public AgentLimits Limits { get; init; } = new();

    /// <summary>Триггеры эскалации версии (agent_versions.escalation_rules).</summary>
    public AgentEscalationRules EscalationRules { get; init; } = new();
}

/// <summary>
/// Тул, доступный агенту: описание и JSON Schema из манифеста коннектора плюс
/// серверные настройки политики/гейта (<see cref="ToolContext"/>).
/// </summary>
/// <param name="Description">Описание для модели (может быть переопределено тенантом).</param>
/// <param name="InputSchema">JSON Schema аргументов тула (строка из манифеста).</param>
/// <param name="Tool">Политика манифеста и переопределения тенанта (Policy Engine).</param>
public sealed record AgentToolConfig(
    string Description,
    string InputSchema,
    ToolContext Tool)
{
    /// <summary>Установка коннектора (connector_installations.id).</summary>
    public Guid InstallationId => Tool.InstallationId;

    /// <summary>Имя тула из манифеста.</summary>
    public string ToolName => Tool.ToolName;
}

/// <summary>
/// Лимиты версии агента (agent_versions.limits): <c>{max_turns, max_tool_calls, max_tokens}</c>.
/// При превышении Runtime эскалирует диалог, а не продолжает бесконечно.
/// </summary>
/// <param name="MaxTurns">Максимум LLM-ходов в сессии.</param>
/// <param name="MaxToolCalls">Максимум вызовов тулов в сессии.</param>
/// <param name="MaxTokens">Максимум токенов (0 — лимит не задан).</param>
public sealed record AgentLimits(
    int MaxTurns = 10,
    int MaxToolCalls = 8,
    long MaxTokens = 0);

/// <summary>
/// Триггеры эскалации (agent_versions.escalation_rules): в v1 учитываются
/// проваленные LLM-ходы. Расширение правил (sentiment и др.) — отдельная задача.
/// </summary>
/// <param name="FailedTurnsThreshold">Порог неудачных ходов до эскалации.</param>
public sealed record AgentEscalationRules(int FailedTurnsThreshold = 3);
