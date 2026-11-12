namespace AgentPlatform.Gateway.Contracts;

/// <summary>
/// Сообщение, пришедшее из канала (тело POST /api/sessions/{tenantKey}/messages).
/// </summary>
/// <param name="Text">Текст сообщения визитера.</param>
public sealed record IncomingChannelMessage(string Text)
{
    /// <summary>
    /// Продолжить существующую сессию. null — Gateway найдёт открытую сессию
    /// визитера или создаст новую (канал о сессиях не знает).
    /// </summary>
    public Guid? SessionId { get; init; }

    /// <summary>
    /// Конкретный канал тенанта. null — активный виджет-канал тенанта.
    /// </summary>
    public Guid? ChannelId { get; init; }

    /// <summary>
    /// user_ref из localStorage виджета (дублирует заголовок X-User-Ref).
    /// </summary>
    public string? UserRef { get; init; }
}

/// <summary>
/// Ответ на ход диалога. Дублирует то, что уходит в SSE-поток: канал может
/// использовать ответ POST (Telegram/тесты) или стрим (виджет).
/// </summary>
/// <param name="SessionId">Сессия платформы (для подписки на стрим).</param>
/// <param name="TenantId">Тенант сессии.</param>
/// <param name="UserRef">Идентификатор визитера (cookie/localStorage/JWT).</param>
/// <param name="Status">Статус сессии: active | awaiting_gate | escalated | degraded.</param>
/// <param name="Outcome">Итог хода: completed | paused_for_gate | escalated | degraded.</param>
/// <param name="Reply">Ответ агента (для degraded — fallback-сообщение).</param>
/// <param name="ApprovalId">Заявка гейта, если сессия ждёт подтверждения.</param>
/// <param name="GateCard">Карточка подтверждения (snake_case JSON), если есть гейт.</param>
public sealed record MessageReplyResponse(
    Guid SessionId,
    Guid TenantId,
    string UserRef,
    string Status,
    string Outcome,
    string? Reply,
    Guid? ApprovalId = null,
    string? GateCard = null);

/// <summary>
/// Результат обработки сообщения: ответ каналу и cookie визитера, которую нужно
/// выставить (только при первом визите — идентификация cookie/localStorage).
/// </summary>
/// <param name="Response">Ответ каналу.</param>
/// <param name="VisitorCookie">Значение cookie user_ref или null, если cookie уже есть.</param>
public sealed record MessageTurn(MessageReplyResponse Response, string? VisitorCookie);
