using AgentPlatform.Core.Dto;

namespace AgentPlatform.Core.Abstractions;

/// <summary>
/// Контракт канала (плагин: widget, telegram, …). Последовательность по документу §6.4:
/// приём сообщения с идентификацией пользователя (<see cref="OnMessage"/>) → привязка
/// к сессии в Gateway → стриминг ответа (<see cref="StreamReply"/>) → карточки
/// подтверждения и эскалации (<see cref="ShowConfirmationCard"/>, <see cref="ShowEscalation"/>).
/// Канал не содержит бизнес-логики: только транспорт и рендеринг платформенных событий.
/// </summary>
public interface IChannelAdapter
{
    /// <summary>
    /// Тип канала в терминах платформы (значение channels.type): widget | telegram | …
    /// Строка, а не enum: новый канал — плагин, а не правка Core.
    /// </summary>
    string ChannelType { get; }

    /// <summary>
    /// Принимает сырой payload канала, проверяет подпись/JWT и идентифицирует визитера,
    /// возвращая нормализованное сообщение платформы. Привязку к сессии (открытие или
    /// возобновление по thread_id) выполняет Gateway — канал о сессиях не знает.
    /// </summary>
    Task<IncomingMessage> OnMessage(ChannelEnvelope envelope, CancellationToken ct = default);

    /// <summary>
    /// Доставляет поток ответа агента (или оператора) получателю. Канал сам выбирает
    /// способ доставки: виджет — SSE-чанки, Telegram — накопление и отправка сообщениями.
    /// </summary>
    /// <param name="conversation">Адрес доставки (сессия + визитер).</param>
    /// <param name="reply">Поток чанков; последний помечен <c>IsFinal</c>.</param>
    Task StreamReply(
        ChannelConversation conversation,
        IAsyncEnumerable<ChannelReplyChunk> reply,
        CancellationToken ct = default);

    /// <summary>
    /// Показывает карточку подтверждения write/destructive-действия (HITL-гейт).
    /// Решение пользователя возвращается в Gateway и передаётся в
    /// <see cref="IOrchestrationRuntime.ResumeAsync"/>.
    /// </summary>
    Task ShowConfirmationCard(
        ChannelConversation conversation,
        ConfirmationCard card,
        CancellationToken ct = default);

    /// <summary>
    /// Показывает уведомление об эскалации: диалог передан оператору (takeover).
    /// </summary>
    Task ShowEscalation(
        ChannelConversation conversation,
        EscalationNotice notice,
        CancellationToken ct = default);
}
