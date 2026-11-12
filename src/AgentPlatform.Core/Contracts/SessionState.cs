using AgentPlatform.Core.Enums;

namespace AgentPlatform.Core.Contracts;

/// <summary>
/// Нейтральное состояние сессии (checkpoints.state):
/// <c>{messages, pending_gate, counters, version}</c>.
/// <para>
/// Это НЕ формат рантайма: смена оркестратора не мигрирует историю сессий.
/// Сериализация и десериализация — только через <see cref="StateContract"/>.
/// </para>
/// </summary>
public sealed record SessionState
{
    /// <summary>Текущая версия контракта состояния.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Версия контракта (валидируется при resume).</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>История диалога на момент чекпоинта.</summary>
    public IReadOnlyList<StateMessage> Messages { get; init; } = [];

    /// <summary>Ожидающий HITL-гейт, если сессия на паузе.</summary>
    public PendingGateState? PendingGate { get; init; }

    /// <summary>Счётчики ходов/тулов/токенов.</summary>
    public SessionCounters Counters { get; init; } = new();
}

/// <summary>Сообщение внутри нейтрального состояния сессии.</summary>
/// <param name="Role">user | assistant | tool | operator | system.</param>
/// <param name="Content">Текст сообщения (ПДн уже замаскированы).</param>
/// <param name="Timestamp">Время сообщения (UTC).</param>
/// <param name="ToolCalls">Вызовы тулов, связанные с сообщением.</param>
public sealed record StateMessage(
    MessageRole Role,
    string Content,
    DateTimeOffset Timestamp,
    IReadOnlyList<StateToolCall>? ToolCalls = null);

/// <summary>Запись о вызове тула в состоянии сессии.</summary>
/// <param name="Name">Имя тула.</param>
/// <param name="ArgsMasked">Аргументы с замаскированными ПДн (JSON-строка).</param>
/// <param name="ResultRef">Ссылка на полный результат в хранилище.</param>
/// <param name="LatencyMs">Задержка вызова, мс.</param>
public sealed record StateToolCall(
    string Name,
    string? ArgsMasked = null,
    string? ResultRef = null,
    int? LatencyMs = null);

/// <summary>Ожидающий подтверждения гейт внутри состояния сессии.</summary>
/// <param name="ApprovalId">Идентификатор заявки approvals.id.</param>
/// <param name="Payload">Данные гейта (аргументы маскированы).</param>
/// <param name="ExpiresAt">Таймаут ожидания решения.</param>
/// <param name="ArgumentsJson">
/// Полные аргументы вызова тула для исполнения после approved. В approvals и в карточку
/// попадает только <see cref="GatePayload.ArgsMasked"/>, поэтому аргументы для resume
/// восстанавливаются из чекпоинта.
/// </param>
public sealed record PendingGateState(
    Guid ApprovalId,
    GatePayload Payload,
    DateTimeOffset? ExpiresAt = null,
    string? ArgumentsJson = null);

/// <summary>Счётчики сессии (лимиты max_turns / max_tool_calls / max_tokens).</summary>
public sealed record SessionCounters(
    int Turns = 0,
    int ToolCalls = 0,
    int FailedTurns = 0,
    long TokensIn = 0,
    long TokensOut = 0);
