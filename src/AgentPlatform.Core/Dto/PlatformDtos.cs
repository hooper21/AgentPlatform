using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Enums;

namespace AgentPlatform.Core.Dto;

/// <summary>
/// Идентификация сессии для оркестратора: версия агента зафиксирована
/// (version pinning) на момент старта сессии.
/// </summary>
public sealed record SessionRef(
    Guid SessionId,
    Guid TenantId,
    Guid AgentId,
    int AgentVersion,
    Guid ChannelId,
    string UserRef);

/// <summary>Результат шага оркестратора.</summary>
/// <param name="Outcome">Итог шага.</param>
/// <param name="Message">Ответ ассистента (если есть).</param>
/// <param name="PendingGate">Ожидающий гейт (если шаг остановлен на gate-узле).</param>
/// <param name="ApprovalId">Идентификатор созданной заявки approvals.id.</param>
/// <param name="GraphSignatureHash">Хеш топологии графа (валидация resume).</param>
/// <param name="Superstep">Номер супершага, к которому относится состояние.</param>
public sealed record RunResult(
    RunOutcome Outcome,
    string? Message,
    GatePayload? PendingGate = null,
    Guid? ApprovalId = null,
    string? GraphSignatureHash = null,
    int Superstep = 0);

/// <summary>Сообщение диалога в терминах платформы.</summary>
public sealed record ConversationMessage(
    long Id,
    Guid SessionId,
    MessageRole Role,
    string Content,
    DateTimeOffset Timestamp);

/// <summary>Нормализованное входящее сообщение визитера.</summary>
public sealed record InboundMessage(
    Guid TenantId,
    Guid ChannelId,
    string UserRef,
    string Text,
    IReadOnlyDictionary<string, string>? Metadata = null,
    DateTimeOffset? ReceivedAt = null);

/// <summary>Исходящее сообщение в канал.</summary>
public sealed record OutboundMessage(string Text, IReadOnlyList<MessageAttachment>? Attachments = null);

/// <summary>Вложение исходящего сообщения.</summary>
public sealed record MessageAttachment(string Kind, string Ref);

/// <summary>Краткая карточка сессии для кабинета/оператора.</summary>
public sealed record SessionSummary(
    Guid Id,
    Guid AgentId,
    int AgentVersion,
    SessionStatus Status,
    string UserRef,
    DateTimeOffset StartedAt,
    DateTimeOffset LastActivityAt);
