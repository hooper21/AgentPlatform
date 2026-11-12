using AgentPlatform.Core.Json;

namespace AgentPlatform.Core.Contracts;

/// <summary>
/// Нейтральный JSON-контракт состояния сессии (checkpoints.state).
/// НЕ формат рантайма: смена оркестратора не должна мигрировать историю сессий.
/// Структура: <c>{messages, pending_gate, counters, version}</c>.
/// </summary>
public sealed record SessionStateContract
{
    /// <summary>Текущая версия контракта состояния.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Версия контракта (для валидации при resume).</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>История диалога на момент чекпоинта.</summary>
    public IReadOnlyList<StateMessage> Messages { get; init; } = [];

    /// <summary>Ожидающий HITL-гейт, если сессия на паузе.</summary>
    public PendingGateState? PendingGate { get; init; }

    /// <summary>Счётчики ходов/тулов/токенов.</summary>
    public SessionCounters Counters { get; init; } = new();

    /// <summary>Сериализует состояние в jsonb-документ.</summary>
    public string ToJson() => PlatformJson.SerializeDb(this);

    /// <summary>Читает состояние из jsonb-документа.</summary>
    public static SessionStateContract? FromJson(string json)
        => string.IsNullOrWhiteSpace(json) ? null : PlatformJson.DeserializeDb<SessionStateContract>(json);

    /// <summary>
    /// Проверяет, что состояние можно возобновить: версия контракта поддерживается,
    /// сообщения непустые. Бросает <see cref="JsonValidationException"/> при нарушении.
    /// </summary>
    public void Validate()
    {
        if (Version != CurrentVersion)
        {
            throw new JsonValidationException(
                "checkpoints.state",
                $"unsupported state version {Version}, expected {CurrentVersion}");
        }

        if (Messages.Any(message => string.IsNullOrWhiteSpace(message.Content)))
        {
            throw new JsonValidationException("checkpoints.state", "messages must not contain empty content");
        }
    }
}

/// <summary>Сообщение внутри нейтрального состояния сессии.</summary>
/// <param name="Role">user | assistant | tool | operator | system.</param>
/// <param name="Content">Текст сообщения (ПДн уже замаскированы).</param>
/// <param name="Timestamp">Время сообщения (UTC).</param>
/// <param name="ToolCalls">Вызовы тулов, связанные с сообщением.</param>
public sealed record StateMessage(
    string Role,
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
/// <param name="Payload">Данные гейта.</param>
/// <param name="ExpiresAt">Таймаут ожидания решения.</param>
public sealed record PendingGateState(
    Guid ApprovalId,
    GatePayload Payload,
    DateTimeOffset? ExpiresAt = null);

/// <summary>Счётчики сессии (лимиты max_turns / max_tool_calls / max_tokens).</summary>
public sealed record SessionCounters(
    int Turns = 0,
    int ToolCalls = 0,
    int FailedTurns = 0,
    long TokensIn = 0,
    long TokensOut = 0);
