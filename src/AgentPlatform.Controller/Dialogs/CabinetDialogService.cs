using AgentPlatform.Controller.Contracts;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Json;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Persistence.Entities;
using AgentPlatform.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentPlatform.Controller.Dialogs;

/// <summary>Ошибка сценария диалога кабинета (HTTP 409/422 на уровне endpoint-а).</summary>
public sealed class CabinetDialogException(string code, string message) : Exception(message)
{
    /// <summary>Машиночитаемый код ошибки для API/UI.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// Диалоги кабинета тенанта через Controller API:
/// сообщение → <see cref="IOrchestrationRuntime.RunAsync"/> → пауза на гейте;
/// решение (Подтвердить/Отклонить) → <see cref="IOrchestrationRuntime.ResumeAsync"/>.
/// <para>
/// Сессия, сообщения, чекпоинты и заявки живут в PostgreSQL, поэтому пауза и
/// подтверждение переживают рестарт Controller между шагами.
/// </para>
/// </summary>
public sealed class CabinetDialogService(
    AgentPlatformDbContext context,
    TenantContext tenantContext,
    IOrchestrationRuntime runtime,
    IApprovalStore approvals,
    ICheckpointStore checkpoints,
    TimeProvider timeProvider,
    ILogger<CabinetDialogService> logger)
{
    /// <summary>Тип служебного канала кабинета (channels.type).</summary>
    public const string CabinetChannelType = "cabinet";

    /// <summary>Максимальная длина сообщения кабинета.</summary>
    public const int MaxMessageLength = 4000;

    private const string ActiveStatus = "active";
    private const string ClosedStatus = "closed";
    private const string AwaitingGateStatus = "awaiting_gate";
    private const string PendingApprovalStatus = "pending";
    private const string DefaultUserRef = "cabinet";

    private readonly AgentPlatformDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    private readonly TenantContext _tenantContext =
        tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));

    private readonly IOrchestrationRuntime _runtime =
        runtime ?? throw new ArgumentNullException(nameof(runtime));

    private readonly IApprovalStore _approvals =
        approvals ?? throw new ArgumentNullException(nameof(approvals));

    private readonly ICheckpointStore _checkpoints =
        checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<CabinetDialogService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Сессии диалогов тенанта (свежие сверху) с карточкой ожидающего гейта.</summary>
    public async Task<IReadOnlyList<CabinetSessionResponse>> ListAsync(
        Guid tenantId,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);

        var sessions = await _context.Sessions
            .AsNoTracking()
            .Where(session => session.TenantId == tenantId)
            .OrderByDescending(session => session.LastActivityAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var agentIds = sessions.Select(session => session.AgentId).Distinct().ToList();
        var agentNames = await _context.Agents
            .AsNoTracking()
            .Where(agent => agent.TenantId == tenantId && agentIds.Contains(agent.Id))
            .ToDictionaryAsync(agent => agent.Id, agent => agent.Name, cancellationToken)
            .ConfigureAwait(false);

        var sessionIds = sessions.Select(session => session.Id).ToList();
        var pending = await _context.Approvals
            .AsNoTracking()
            .Where(approval => approval.TenantId == tenantId
                && approval.Status == PendingApprovalStatus
                && sessionIds.Contains(approval.SessionId))
            .OrderByDescending(approval => approval.RequestedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var pendingBySession = pending
            .GroupBy(approval => approval.SessionId)
            .ToDictionary(group => group.Key, group => ToApprovalResponse(group.First()));

        return sessions
            .Select(session => new CabinetSessionResponse(
                session.Id,
                session.AgentId,
                agentNames.GetValueOrDefault(session.AgentId, "unknown"),
                session.Status,
                session.UserRef,
                session.StartedAt,
                session.LastActivityAt,
                pendingBySession.GetValueOrDefault(session.Id)))
            .ToList();
    }

    /// <summary>Сессия с сообщениями и карточкой гейта (null — не найдена в тенанте).</summary>
    public async Task<CabinetDialogResponse?> GetAsync(
        Guid tenantId,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);

        var session = await FindSessionAsync(tenantId, sessionId, cancellationToken).ConfigureAwait(false);

        return session is null
            ? null
            : await BuildDialogAsync(tenantId, session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Начинает диалог: создаёт сессию и выполняет первый ход агента.</summary>
    public async Task<CabinetDialogResponse> StartAsync(
        Guid tenantId,
        CabinetDialogStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireTenant(tenantId);
        ValidateMessage(request.Message);

        if (request.AgentId == Guid.Empty)
        {
            throw new CabinetDialogException("agent_id_required", "agentId is required.");
        }

        var agent = await _context.Agents
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.TenantId == tenantId && candidate.Id == request.AgentId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CabinetDialogException(
                "agent_not_found",
                $"Agent {request.AgentId} is not found in the tenant.");

        var channel = await ResolveCabinetChannelAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();

        var session = new AgentSession
        {
            TenantId = tenantId,
            AgentId = agent.Id,
            AgentVersion = agent.CurrentVersion,
            ChannelId = channel.Id,
            UserRef = string.IsNullOrWhiteSpace(request.UserRef)
                ? DefaultUserRef
                : request.UserRef.Trim(),
            Status = ActiveStatus,
            Meta = "{}",
            StartedAt = now,
            LastActivityAt = now
        };

        _context.Sessions.Add(session);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return await RunTurnAsync(tenantId, session, request.Message.Trim(), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Продолжает диалог сообщением кабинета (ожидающий гейт блокирует ход).</summary>
    public async Task<CabinetDialogResponse?> SendMessageAsync(
        Guid tenantId,
        Guid sessionId,
        string message,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        ValidateMessage(message);

        var session = await FindSessionAsync(tenantId, sessionId, cancellationToken).ConfigureAwait(false);

        if (session is null)
        {
            return null;
        }

        if (string.Equals(session.Status, ClosedStatus, StringComparison.Ordinal))
        {
            throw new CabinetDialogException("session_closed", "The session is closed; start a new dialog.");
        }

        if (await _approvals.FindPendingAsync(tenantId, sessionId, cancellationToken)
                .ConfigureAwait(false) is not null)
        {
            throw new CabinetDialogException(
                "gate_pending",
                "The session is waiting for a gate decision; approve or reject it first.");
        }

        return await RunTurnAsync(tenantId, session, message.Trim(), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Подтверждает/отклоняет ожидающий гейт: Controller вызывает ResumeAsync,
    /// решение фиксируется в approvals внутри GateExecutor (аудит до действия).
    /// </summary>
    public async Task<CabinetDialogResponse?> DecideAsync(
        Guid tenantId,
        Guid sessionId,
        Guid approvalId,
        CabinetApprovalDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireTenant(tenantId);

        var status = ParseDecision(request.Decision);

        var session = await FindSessionAsync(tenantId, sessionId, cancellationToken).ConfigureAwait(false);

        if (session is null)
        {
            return null;
        }

        if (string.Equals(session.Status, ClosedStatus, StringComparison.Ordinal))
        {
            throw new CabinetDialogException("session_closed", "The session is closed.");
        }

        var approval = await _approvals.FindAsync(tenantId, approvalId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CabinetDialogException(
                "approval_not_found",
                $"Approval {approvalId} is not found in the tenant.");

        if (approval.SessionId != sessionId)
        {
            throw new CabinetDialogException(
                "approval_session_mismatch",
                $"Approval {approvalId} belongs to another session.");
        }

        if (!approval.IsPending)
        {
            throw new CabinetDialogException(
                "approval_already_decided",
                $"Approval {approvalId} is already {approval.Status}.");
        }

        var beforeMessages = await CountCheckpointMessagesAsync(tenantId, sessionId, cancellationToken)
            .ConfigureAwait(false);

        var actorId = await ResolveDecisionActorAsync(tenantId, request.ActorId, cancellationToken)
            .ConfigureAwait(false);

        var sessionContext = CreateSessionContext(session);

        await _runtime.ResumeAsync(
                sessionContext,
                new Decision(
                    status,
                    ApprovalDecisionKind.Operator,
                    actorId,
                    request.Comment,
                    _timeProvider.GetUtcNow()))
            .ConfigureAwait(false);

        await AppendNewStateMessagesAsync(tenantId, sessionId, beforeMessages, cancellationToken)
            .ConfigureAwait(false);

        var state = await ReadLatestStateAsync(tenantId, sessionId, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();

        session.Status = state?.PendingGate is null ? ActiveStatus : AwaitingGateStatus;
        session.LastActivityAt = now;
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Cabinet gate {ApprovalId} in session {SessionId} decided as {Decision} by operator {ActorId}",
            approvalId,
            sessionId,
            status.ToDbValue(),
            actorId);

        return await BuildDialogAsync(tenantId, session, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CabinetDialogResponse> RunTurnAsync(
        Guid tenantId,
        AgentSession session,
        string text,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        _context.Messages.Add(new Message
        {
            SessionId = session.Id,
            TenantId = tenantId,
            Role = MessageRole.User.ToDbValue(),
            Content = text,
            Ts = now
        });

        session.LastActivityAt = now;
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var result = await _runtime
            .RunAsync(
                CreateSessionContext(session),
                new IncomingMessage(tenantId, session.ChannelId, session.UserRef, text, ReceivedAt: now),
                cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            _context.Messages.Add(new Message
            {
                SessionId = session.Id,
                TenantId = tenantId,
                Role = MessageRole.Assistant.ToDbValue(),
                Content = result.Message,
                Ts = _timeProvider.GetUtcNow()
            });
        }

        session.Status = MapStatus(result.Outcome);
        session.LastActivityAt = _timeProvider.GetUtcNow();
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return await BuildDialogAsync(tenantId, session, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Channel> ResolveCabinetChannelAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var channel = await _context.Channels
            .FirstOrDefaultAsync(
                candidate => candidate.TenantId == tenantId
                    && candidate.Type == CabinetChannelType,
                cancellationToken)
            .ConfigureAwait(false);

        if (channel is not null)
        {
            return channel;
        }

        var now = _timeProvider.GetUtcNow();

        channel = new Channel
        {
            TenantId = tenantId,
            Type = CabinetChannelType,
            Config = "{}",
            Status = ActiveStatus,
            CreatedAt = now,
            UpdatedAt = now
        };

        _context.Channels.Add(channel);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return channel;
    }

    private Task<AgentSession?> FindSessionAsync(
        Guid tenantId,
        Guid sessionId,
        CancellationToken cancellationToken)
        => _context.Sessions.FirstOrDefaultAsync(
            session => session.TenantId == tenantId && session.Id == sessionId,
            cancellationToken);

    private async Task<CabinetDialogResponse> BuildDialogAsync(
        Guid tenantId,
        AgentSession session,
        CancellationToken cancellationToken)
    {
        var agentName = await _context.Agents
            .AsNoTracking()
            .Where(agent => agent.TenantId == tenantId && agent.Id == session.AgentId)
            .Select(agent => agent.Name)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? "unknown";

        var messages = await _context.Messages
            .AsNoTracking()
            .Where(message => message.TenantId == tenantId && message.SessionId == session.Id)
            .OrderBy(message => message.Ts)
            .ThenBy(message => message.Id)
            .Select(message => new CabinetMessageResponse(
                message.Id,
                message.Role,
                message.Content,
                message.Ts))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var pending = await _context.Approvals
            .AsNoTracking()
            .Where(approval => approval.TenantId == tenantId
                && approval.SessionId == session.Id
                && approval.Status == PendingApprovalStatus)
            .OrderByDescending(approval => approval.RequestedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return new CabinetDialogResponse(
            session.Id,
            session.AgentId,
            agentName,
            session.Status,
            session.UserRef,
            session.StartedAt,
            session.LastActivityAt,
            messages,
            pending is null ? null : ToApprovalResponse(pending));
    }

    private static CabinetApprovalResponse ToApprovalResponse(Approval approval)
    {
        var payload = GatePayload.FromJson(approval.GatePayload);

        return new CabinetApprovalResponse(
            approval.Id,
            payload?.Tool ?? "unknown",
            payload?.HumanDescription ?? string.Empty,
            payload?.ArgsMasked?.ToJsonString() ?? "{}",
            approval.Status,
            approval.RequestedAt,
            approval.ExpiresAt ?? approval.RequestedAt.AddHours(24),
            payload?.UndoHint);
    }

    private static SessionContext CreateSessionContext(AgentSession session) => new(
        session.Id,
        session.TenantId,
        session.AgentId,
        session.AgentVersion,
        session.ChannelId,
        session.UserRef,
        ParseStatus(session.Status));

    private static SessionStatus ParseStatus(string statusToken)
        => EnumTokens.TryParseDbValue<SessionStatus>(statusToken, out var status)
            ? status
            : SessionStatus.Active;

    private static string MapStatus(RunOutcome outcome) => outcome switch
    {
        RunOutcome.PausedForGate => AwaitingGateStatus,
        RunOutcome.Escalated => SessionStatus.Escalated.ToDbValue(),
        RunOutcome.Degraded => SessionStatus.Degraded.ToDbValue(),
        _ => ActiveStatus
    };

    private async Task<int> CountCheckpointMessagesAsync(
        Guid tenantId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var state = await ReadLatestStateAsync(tenantId, sessionId, cancellationToken).ConfigureAwait(false);

        return state?.Messages.Count ?? 0;
    }

    /// <summary>
    /// Переносит в журнал диалога сообщения, появившиеся в чекпоинте при resume
    /// (tool/assistant/system). История модели хранится в чекпоинте, журнал — для людей.
    /// </summary>
    private async Task AppendNewStateMessagesAsync(
        Guid tenantId,
        Guid sessionId,
        int alreadyLogged,
        CancellationToken cancellationToken)
    {
        var state = await ReadLatestStateAsync(tenantId, sessionId, cancellationToken).ConfigureAwait(false);

        if (state is null || state.Messages.Count <= alreadyLogged)
        {
            return;
        }

        foreach (var message in state.Messages.Skip(alreadyLogged))
        {
            _context.Messages.Add(new Message
            {
                SessionId = sessionId,
                TenantId = tenantId,
                Role = message.Role.ToDbValue(),
                Content = message.Content,
                ToolCalls = message.ToolCalls is null
                    ? null
                    : PlatformJson.SerializeDb(message.ToolCalls),
                Ts = message.Timestamp
            });
        }

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SessionState?> ReadLatestStateAsync(
        Guid tenantId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var checkpoint = await _checkpoints
            .GetLatestAsync(tenantId, sessionId, cancellationToken)
            .ConfigureAwait(false);

        if (checkpoint is null)
        {
            return null;
        }

        return StateContract.Deserialize(checkpoint.State);
    }

    /// <summary>
    /// Решение по гейту принимает пользователь кабинета с ролью admin/operator:
    /// роль проверяется серверно по users.role, а не только в UI.
    /// </summary>
    private async Task<Guid> ResolveDecisionActorAsync(
        Guid tenantId,
        Guid? actorId,
        CancellationToken cancellationToken)
    {
        if (actorId is not { } candidate || candidate == Guid.Empty)
        {
            throw new CabinetDialogException(
                "actor_required",
                "actorId (пользователь кабинета) обязателен для решения по гейту.");
        }

        var user = await _context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidateUser => candidateUser.TenantId == tenantId && candidateUser.Id == candidate,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CabinetDialogException(
                "actor_not_found",
                $"User {candidate} is not found in the tenant.");

        if (!EnumTokens.TryParseDbValue<UserRole>(user.Role, out var role)
            || role == UserRole.Viewer)
        {
            throw new CabinetDialogException(
                "actor_forbidden",
                "Gate decisions are allowed for admin/operator roles only.");
        }

        return candidate;
    }

    private static ApprovalStatus ParseDecision(string? decision)
    {
        var normalized = decision?.Trim().ToLowerInvariant();

        return normalized switch
        {
            "approve" or "approved" or "confirm" or "подтвердить" => ApprovalStatus.Approved,
            "reject" or "rejected" or "decline" or "отклонить" => ApprovalStatus.Rejected,
            _ => throw new CabinetDialogException(
                "invalid_decision",
                "decision must be 'approve' or 'reject'.")
        };
    }

    private static void ValidateMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new CabinetDialogException("message_required", "message is required.");
        }

        if (message.Trim().Length > MaxMessageLength)
        {
            throw new CabinetDialogException(
                "message_too_long",
                $"message must be at most {MaxMessageLength} characters.");
        }
    }

    private void RequireTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("tenant_id is required", nameof(tenantId));
        }

        _tenantContext.SetTenant(tenantId);
    }
}
