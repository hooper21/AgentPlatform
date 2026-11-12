namespace AgentPlatform.Controller.Contracts;

/// <summary>Начало диалога из кабинета: агент + первое сообщение.</summary>
public sealed record CabinetDialogStartRequest(
    Guid AgentId,
    string Message,
    string? UserRef = null);

/// <summary>Продолжение диалога сообщением из кабинета.</summary>
public sealed record CabinetDialogMessageRequest(string Message);

/// <summary>
/// Решение по ожидающему гейту: approve | reject. ActorId — пользователь кабинета
/// (users.id); неизвестный пользователь не подставляется в approvals.decided_by.
/// </summary>
public sealed record CabinetApprovalDecisionRequest(
    string Decision,
    string? Comment = null,
    Guid? ActorId = null);

/// <summary>Карточка ожидающего гейта (аргументы замаскированы).</summary>
public sealed record CabinetApprovalResponse(
    Guid ApprovalId,
    string Tool,
    string HumanDescription,
    string ArgsMasked,
    string Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt,
    string? UndoHint);

/// <summary>Сообщение журнала диалога.</summary>
public sealed record CabinetMessageResponse(
    long Id,
    string Role,
    string Content,
    DateTimeOffset Timestamp);

/// <summary>Сессия диалога в списке кабинета.</summary>
public sealed record CabinetSessionResponse(
    Guid Id,
    Guid AgentId,
    string AgentName,
    string Status,
    string UserRef,
    DateTimeOffset StartedAt,
    DateTimeOffset LastActivityAt,
    CabinetApprovalResponse? PendingApproval);

/// <summary>Сессия диалога с сообщениями и ожидающим гейтом.</summary>
public sealed record CabinetDialogResponse(
    Guid SessionId,
    Guid AgentId,
    string AgentName,
    string Status,
    string UserRef,
    DateTimeOffset StartedAt,
    DateTimeOffset LastActivityAt,
    IReadOnlyList<CabinetMessageResponse> Messages,
    CabinetApprovalResponse? PendingApproval);
