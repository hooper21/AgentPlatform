using System.Text.Json.Nodes;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Json;

namespace AgentPlatform.Core.Dto;

/// <summary>
/// Заявка HITL-гейта (строка таблицы approvals) в терминах платформы.
/// Создаётся gate-узлом до паузы и живёт в БД, поэтому подтверждение переживает
/// рестарт сервиса: resume читает заявку по <see cref="Id"/> из чекпоинта.
/// </summary>
/// <param name="Id">approvals.id.</param>
/// <param name="TenantId">Тенант заявки.</param>
/// <param name="SessionId">Сессия, в которой возник гейт.</param>
/// <param name="Payload">Данные гейта (аргументы маскированы).</param>
/// <param name="Status">pending | approved | rejected | expired | superseded.</param>
/// <param name="RequestedAt">Момент создания заявки (UTC).</param>
/// <param name="ExpiresAt">Таймаут решения (UTC): по настройке тенанта, по умолчанию 24 ч.</param>
/// <param name="DecidedBy">Пользователь кабинета, принявший решение (null — визитер/система).</param>
/// <param name="DecidedByKind">operator | user | system.</param>
/// <param name="Comment">Комментарий к решению.</param>
/// <param name="DecidedAt">Момент решения (UTC).</param>
public sealed record ApprovalRequest(
    Guid Id,
    Guid TenantId,
    Guid SessionId,
    GatePayload Payload,
    ApprovalStatus Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt,
    Guid? DecidedBy = null,
    ApprovalDecisionKind? DecidedByKind = null,
    string? Comment = null,
    DateTimeOffset? DecidedAt = null)
{
    /// <summary>true — решение ещё не принято и таймаут не истёк по статусу.</summary>
    public bool IsPending => Status == ApprovalStatus.Pending;

    /// <summary>Карточка подтверждения для канала/кабинета (без рендеринга).</summary>
    public ApprovalCard ToCard() => new(
        Id,
        TenantId,
        SessionId,
        Payload.Tool,
        Payload.ArgsMasked,
        Payload.HumanDescription,
        Status,
        RequestedAt,
        ExpiresAt,
        Payload.UndoHint);

    /// <summary>Карточка подтверждения как JSON (snake_case) для <see cref="RunResult.GateCard"/>.</summary>
    public string ToCardJson() => ToCard().ToJson();
}

/// <summary>
/// Карточка HITL-подтверждения. Runtime возвращает её как JSON в
/// <see cref="RunResult.GateCard"/>; рендеринг в каналах — фаза 2.
/// В карточке только маскированные аргументы.
/// </summary>
/// <param name="ApprovalId">approvals.id.</param>
/// <param name="TenantId">Тенант.</param>
/// <param name="SessionId">Сессия.</param>
/// <param name="Tool">Имя тула.</param>
/// <param name="ArgsMasked">Аргументы с замаскированными ПДн.</param>
/// <param name="HumanDescription">Человекочитаемое описание действия.</param>
/// <param name="Status">Статус заявки на момент формирования карточки.</param>
/// <param name="CreatedAt">Момент создания заявки (UTC).</param>
/// <param name="ExpiresAt">Момент истечения подтверждения (UTC).</param>
/// <param name="UndoHint">Подсказка, как отменить действие.</param>
public sealed record ApprovalCard(
    Guid ApprovalId,
    Guid TenantId,
    Guid SessionId,
    string Tool,
    JsonNode? ArgsMasked,
    string HumanDescription,
    ApprovalStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? UndoHint = null)
{
    /// <summary>Сериализует карточку в JSON (snake_case) для каналов.</summary>
    public string ToJson() => PlatformJson.SerializeDb(this);

    /// <summary>Читает карточку из JSON (snake_case).</summary>
    public static ApprovalCard? FromJson(string json)
        => string.IsNullOrWhiteSpace(json) ? null : PlatformJson.DeserializeDb<ApprovalCard>(json);
}
