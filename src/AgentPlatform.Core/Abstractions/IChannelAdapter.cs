using AgentPlatform.Core.Dto;

namespace AgentPlatform.Core.Abstractions;

/// <summary>
/// Контракт канала (widget, telegram, плагины). Канал принимает входящее
/// сообщение от визитера и доставляет ответ агента.
/// </summary>
public interface IChannelAdapter
{
    /// <summary>Тип канала в терминах платформы (widget | telegram | ...).</summary>
    string ChannelType { get; }

    /// <summary>Нормализует входящий payload канала в сообщение платформы.</summary>
    Task<InboundMessage> ParseAsync(ChannelEnvelope envelope, CancellationToken cancellationToken = default);

    /// <summary>Доставляет ответ агента (или оператора) получателю в канале.</summary>
    Task SendAsync(Guid tenantId, Recipient recipient, OutboundMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Сырой payload, пришедший от канала.</summary>
/// <param name="TenantId">Тенант, которому принадлежит канал.</param>
/// <param name="ChannelId">Идентификатор канала.</param>
/// <param name="UserRef">Идентификатор визитера в терминах канала.</param>
/// <param name="Body">Тело сообщения.</param>
/// <param name="Headers">Заголовки/подпись запроса (например, JWT виджета).</param>
public sealed record ChannelEnvelope(
    Guid TenantId,
    Guid ChannelId,
    string UserRef,
    string Body,
    IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>Адресат доставки в канале.</summary>
public sealed record Recipient(Guid ChannelId, string UserRef);
