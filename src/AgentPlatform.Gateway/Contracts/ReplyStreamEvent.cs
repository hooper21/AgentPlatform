namespace AgentPlatform.Gateway.Contracts;

/// <summary>
/// Событие SSE-потока ответа (GET /api/sessions/{sessionId}/stream).
/// Тип события дублируется в имени SSE-события (<c>event: delta</c>).
/// </summary>
/// <param name="Type">status | delta | gate | done.</param>
/// <param name="Delta">Порция текста ответа (для delta).</param>
/// <param name="Status">Статус сессии (active | awaiting_gate | escalated | degraded).</param>
/// <param name="Outcome">Итог хода (для done).</param>
/// <param name="Message">Полный текст ответа (для done; null — ответа нет, например гейт).</param>
/// <param name="ApprovalId">Заявка гейта (для gate).</param>
/// <param name="GateCard">Карточка подтверждения (snake_case JSON, для gate).</param>
public sealed record ReplyStreamEvent(
    string Type,
    string? Delta = null,
    string? Status = null,
    string? Outcome = null,
    string? Message = null,
    Guid? ApprovalId = null,
    string? GateCard = null)
{
    /// <summary>Ход принят в обработку.</summary>
    public const string StatusType = "status";

    /// <summary>Порция ответа.</summary>
    public const string DeltaType = "delta";

    /// <summary>Требуется подтверждение HITL-гейта.</summary>
    public const string GateType = "gate";

    /// <summary>Ход завершён: поток закрывается.</summary>
    public const string DoneType = "done";

    /// <summary>Уведомление о начале обработки.</summary>
    public static ReplyStreamEvent Processing(string status)
        => new(StatusType, Status: status);

    /// <summary>Порция текста ответа.</summary>
    public static ReplyStreamEvent Text(string delta)
        => new(DeltaType, Delta: delta);

    /// <summary>Карточка подтверждения write/destructive-действия.</summary>
    public static ReplyStreamEvent Confirmation(Guid? approvalId, string? gateCard)
        => new(GateType, ApprovalId: approvalId, GateCard: gateCard);

    /// <summary>Завершение хода (последнее событие потока).</summary>
    public static ReplyStreamEvent Done(string status, string outcome, string? message)
        => new(DoneType, Status: status, Outcome: outcome, Message: message);
}
