using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Enums;

namespace AgentPlatform.Core.Dto;

/// <summary>
/// Контекст сессии для оркестратора и Policy Engine: версия агента зафиксирована
/// (version pinning) на момент старта сессии и не меняется при resume.
/// </summary>
/// <param name="SessionId">Идентификатор сессии (sessions.id / thread_id).</param>
/// <param name="TenantId">Тенант сессии.</param>
/// <param name="AgentId">Агент сессии.</param>
/// <param name="AgentVersion">Версия агента, с которой начата сессия.</param>
/// <param name="ChannelId">Канал, через который идёт диалог.</param>
/// <param name="UserRef">Идентификатор визитера в терминах канала.</param>
/// <param name="Status">Текущий статус сессии.</param>
public sealed record SessionContext(
    Guid SessionId,
    Guid TenantId,
    Guid AgentId,
    int AgentVersion,
    Guid ChannelId,
    string UserRef,
    SessionStatus Status = SessionStatus.Active);

/// <summary>Результат шага оркестратора.</summary>
/// <param name="Outcome">Итог шага.</param>
/// <param name="Message">Ответ ассистента (если есть).</param>
/// <param name="PendingGate">Ожидающий гейт (если шаг остановлен на gate-узле).</param>
/// <param name="ApprovalId">Идентификатор созданной заявки approvals.id.</param>
/// <param name="GraphSignatureHash">Хеш топологии графа (валидация resume).</param>
/// <param name="Superstep">Номер супершага, к которому относится состояние.</param>
/// <param name="GateCard">
/// Карточка подтверждения как JSON (snake_case) для каналов/кабинета.
/// Runtime не рендерит карточку: рендеринг в каналах — фаза 2.
/// </param>
public sealed record RunResult(
    RunOutcome Outcome,
    string? Message,
    GatePayload? PendingGate = null,
    Guid? ApprovalId = null,
    string? GraphSignatureHash = null,
    int Superstep = 0,
    string? GateCard = null);

/// <summary>Сообщение диалога в терминах платформы.</summary>
public sealed record ConversationMessage(
    long Id,
    Guid SessionId,
    MessageRole Role,
    string Content,
    DateTimeOffset Timestamp);

/// <summary>
/// Нормализованное входящее сообщение визитера — результат
/// <see cref="Abstractions.IChannelAdapter.OnMessage"/>.
/// </summary>
/// <param name="TenantId">Тенант, определённый Gateway по ключу канала.</param>
/// <param name="ChannelId">Канал, из которого пришло сообщение.</param>
/// <param name="UserRef">Идентификатор визитера в терминах канала.</param>
/// <param name="Text">Текст сообщения (ПДн маскируются до записи в чекпоинт).</param>
/// <param name="Metadata">Дополнительные атрибуты канала (не влияют на политики).</param>
/// <param name="ReceivedAt">Момент получения (UTC).</param>
public sealed record IncomingMessage(
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
