using System.Text.Json.Nodes;

namespace AgentPlatform.Core.Dto;

/// <summary>
/// Сырой входящий payload канала (вебхук виджета/Telegram) до нормализации.
/// </summary>
/// <param name="TenantId">
/// Тенант, определённый Gateway по ключу канала. В теле вебхука tenant_id не принимается:
/// идентификация тенанта — не ответственность канала.
/// </param>
/// <param name="ChannelId">Идентификатор канала (channels.id).</param>
/// <param name="Body">Тело вебхука в формате канала (JSON-строка).</param>
/// <param name="Headers">Заголовки запроса: подпись, JWT виджета, токен бота.</param>
public sealed record ChannelEnvelope(
    Guid TenantId,
    Guid ChannelId,
    string Body,
    IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>Адрес доставки в канале: сессия платформы + визитер канала.</summary>
/// <param name="TenantId">Тенант сессии.</param>
/// <param name="SessionId">Идентификатор сессии (для корреляции карточек и стрима).</param>
/// <param name="ChannelId">Канал доставки.</param>
/// <param name="UserRef">Идентификатор визитера в терминах канала.</param>
public sealed record ChannelConversation(
    Guid TenantId,
    Guid SessionId,
    Guid ChannelId,
    string UserRef);

/// <summary>Чанк стриминга ответа агента (или оператора) в канал.</summary>
/// <param name="Delta">Очередная порция текста.</param>
/// <param name="IsFinal">true — последний чанк, канал обязан завершить сообщение.</param>
public sealed record ChannelReplyChunk(string Delta, bool IsFinal = false);

/// <summary>
/// Карточка подтверждения HITL-гейта для канала. Собирается из
/// <see cref="Contracts.GatePayload"/> и заявки approvals.
/// </summary>
/// <param name="ApprovalId">Идентификатор заявки approvals.id.</param>
/// <param name="Tool">Имя тула, требующего подтверждения.</param>
/// <param name="HumanDescription">Человекочитаемое описание действия.</param>
/// <param name="ArgsMasked">Аргументы с замаскированными ПДн.</param>
/// <param name="UndoHint">Подсказка, как отменить действие.</param>
/// <param name="ExpiresAt">Момент, после которого заявка истекает.</param>
public sealed record ConfirmationCard(
    Guid ApprovalId,
    string Tool,
    string HumanDescription,
    JsonNode? ArgsMasked = null,
    string? UndoHint = null,
    DateTimeOffset? ExpiresAt = null);

/// <summary>Уведомление об эскалации/передаче диалога оператору (takeover).</summary>
/// <param name="Reason">Триггер эскалации (из escalation_rules: no_answer, user_request, …).</param>
/// <param name="Summary">Контекст диалога для оператора и визитера.</param>
/// <param name="FallbackMessage">Текст для визитера, если оператор недоступен.</param>
/// <param name="OperatorId">Оператор, принявший диалог (null — ещё в очереди).</param>
public sealed record EscalationNotice(
    string Reason,
    string? Summary = null,
    string? FallbackMessage = null,
    Guid? OperatorId = null);
