using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;
using AgentPlatform.Gateway.Contracts;
using AgentPlatform.Gateway.Errors;
using AgentPlatform.Gateway.RateLimiting;
using AgentPlatform.Gateway.Resilience;
using AgentPlatform.Gateway.Security;
using AgentPlatform.Infrastructure.Persistence.Entities;
using AgentPlatform.Infrastructure.Tenancy;

namespace AgentPlatform.Gateway.Sessions;

/// <summary>
/// Приём сообщения канала: JWT виджета → тенант → rate-limit → визитер → сессия →
/// ход агента → публикация ответа в SSE-поток.
/// <para>
/// Класс не работает с HttpContext: данные запроса приходят в
/// <see cref="ChannelRequest"/>, поэтому pipeline тестируется без веб-сервера.
/// </para>
/// <para>
/// Недоступность LLM обрабатывается двумя уровнями: повторы вызова модели
/// (<see cref="RetryingChatClient"/>) и итог degraded от Runtime. Gateway заменяет
/// ответ fallback-сообщением («менеджер ответит лично») и помечает сессию degraded.
/// </para>
/// </summary>
public sealed class GatewayMessagePipeline(
    WidgetJwtAuthenticator authenticator,
    IGlobalRepository<Tenant> tenants,
    ITenantRateLimiter rateLimiter,
    SessionBinder sessions,
    DialogLog dialogLog,
    IOrchestrationRuntime runtime,
    ReplyStreamHub streamHub,
    TenantContext tenantContext,
    LlmResilienceOptions resilience,
    ILogger<GatewayMessagePipeline> logger)
{
    /// <summary>Максимальная длина сообщения визитера (защита от злоупотреблений).</summary>
    public const int MaxMessageLength = 4000;

    private readonly WidgetJwtAuthenticator _authenticator =
        authenticator ?? throw new ArgumentNullException(nameof(authenticator));

    private readonly IGlobalRepository<Tenant> _tenants =
        tenants ?? throw new ArgumentNullException(nameof(tenants));

    private readonly ITenantRateLimiter _rateLimiter =
        rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));

    private readonly SessionBinder _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

    private readonly DialogLog _dialogLog = dialogLog ?? throw new ArgumentNullException(nameof(dialogLog));

    private readonly IOrchestrationRuntime _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    private readonly ReplyStreamHub _streamHub = streamHub ?? throw new ArgumentNullException(nameof(streamHub));

    private readonly TenantContext _tenantContext =
        tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));

    private readonly LlmResilienceOptions _resilience =
        resilience ?? throw new ArgumentNullException(nameof(resilience));

    private readonly ILogger<GatewayMessagePipeline> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Обрабатывает сообщение канала: выполняет ход агента и публикует ответ в поток сессии.
    /// </summary>
    /// <param name="tenantKey">tenant_key из маршрута (сверяется с JWT виджета).</param>
    /// <param name="request">Данные запроса канала (токен, cookie, localStorage).</param>
    /// <param name="message">Сообщение визитера.</param>
    /// <param name="cancellationToken">Отмена (disconnect клиента, graceful shutdown).</param>
    public async Task<MessageTurn> AcceptAsync(
        string tenantKey,
        ChannelRequest request,
        IncomingChannelMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(message);

        var text = message.Text?.Trim() ?? string.Empty;

        if (text.Length == 0)
        {
            throw GatewayException.BadRequest("message_text_required", "message.text must not be empty.");
        }

        if (text.Length > MaxMessageLength)
        {
            throw GatewayException.BadRequest(
                "message_too_long",
                $"message.text must not exceed {MaxMessageLength} characters.");
        }

        // 1. JWT виджета: HS256, секрет тенанта; tenant_key из пути обязан совпасть с claim.
        var identity = _authenticator.Authenticate(tenantKey, request.WidgetToken);

        // 2. Тенант существует и не заблокирован.
        var tenant = await _tenants
            .FindAsync(identity.TenantId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw GatewayException.NotFound("tenant_not_found", $"Tenant '{identity.TenantKey}' is not found.");

        if (!string.Equals(tenant.Status, TenantStatus.Active.ToDbValue(), StringComparison.Ordinal))
        {
            throw GatewayException.Forbidden(
                "tenant_blocked",
                "Tenant is blocked: widget messages are not accepted.");
        }

        // 3. Rate-limit на тенанта: проверяется до запуска агента.
        var decision = await _rateLimiter
            .AcquireAsync(identity.TenantId, cancellationToken)
            .ConfigureAwait(false);

        if (!decision.Allowed)
        {
            throw GatewayException.TooManyRequests(
                "Too many messages for this tenant, retry later.",
                decision.RetryAfter);
        }

        // 4. Идентификация визитера: JWT → localStorage → cookie → новый идентификатор.
        var visitor = VisitorIdentityResolver.Resolve(
            identity.UserRef,
            request.UserRef ?? message.UserRef,
            request.VisitorCookieRef);

        // 5. Сессия: tenant-контекст нужен tenant-фильтрованным репозиториям.
        _tenantContext.SetTenant(identity.TenantId);

        var session = await _sessions
            .ResolveAsync(identity.TenantId, visitor.UserRef, message.SessionId, message.ChannelId, cancellationToken)
            .ConfigureAwait(false);

        var sessionStatus = ParseStatus(session.Status);

        if (sessionStatus == SessionStatus.AwaitingGate)
        {
            throw GatewayException.Conflict(
                "session_awaiting_gate",
                "Session is waiting for a confirmation decision; resume the gate instead of sending a new message.");
        }

        _streamHub.StartTurn(session.Id);
        _streamHub.Publish(session.Id, ReplyStreamEvent.Processing(sessionStatus.ToDbValue()));

        await _dialogLog
            .AppendAsync(session.Id, MessageRole.User, text, cancellationToken)
            .ConfigureAwait(false);

        return await RunTurnAsync(identity, session, sessionStatus, text, visitor, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<MessageTurn> RunTurnAsync(
        WidgetIdentity identity,
        AgentSession session,
        SessionStatus sessionStatus,
        string text,
        VisitorIdentity visitor,
        CancellationToken cancellationToken)
    {
        var context = new SessionContext(
            session.Id,
            identity.TenantId,
            session.AgentId,
            session.AgentVersion,
            session.ChannelId,
            session.UserRef,
            sessionStatus);

        var incoming = new IncomingMessage(
            identity.TenantId,
            session.ChannelId,
            session.UserRef,
            text,
            ReceivedAt: DateTimeOffset.UtcNow);

        var result = await RunSafelyAsync(context, incoming, identity, session.Id, cancellationToken)
            .ConfigureAwait(false);

        // Takeover: если диалог ведёт оператор, агент не переключает статус обратно.
        var status = sessionStatus == SessionStatus.Operator
            ? SessionStatus.Operator
            : MapStatus(result.Outcome);

        var reply = result.Message;

        if (result.Outcome == RunOutcome.Degraded)
        {
            // LLM недоступна после всех повторов: визитеру — fallback, сессия помечается degraded.
            reply = _resilience.FallbackReply;
            _logger.LogWarning(
                "LLM is unavailable: tenant {TenantId}, session {SessionId} is marked degraded and receives the fallback reply",
                identity.TenantId,
                session.Id);
        }

        await _sessions.SetStatusAsync(session, status, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(reply))
        {
            await _dialogLog
                .AppendAsync(session.Id, MessageRole.Assistant, reply, cancellationToken)
                .ConfigureAwait(false);
        }

        PublishReply(session.Id, result, status, reply);

        return new MessageTurn(
            new MessageReplyResponse(
                session.Id,
                identity.TenantId,
                session.UserRef,
                status.ToDbValue(),
                OutcomeToken(result.Outcome),
                reply,
                result.ApprovalId,
                result.GateCard),
            visitor.IsNew ? visitor.UserRef : null);
    }

    private async Task<RunResult> RunSafelyAsync(
        SessionContext context,
        IncomingMessage incoming,
        WidgetIdentity identity,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _runtime.RunAsync(context, incoming, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Ошибка конфигурации/графа: это не «LLM недоступна», fallback её не маскирует.
            _logger.LogError(
                exception,
                "Agent run failed: tenant {TenantId}, session {SessionId}",
                identity.TenantId,
                sessionId);

            throw GatewayException.ServiceUnavailable(
                "agent_unavailable",
                "Agent cannot process the message right now.");
        }
    }

    private void PublishReply(Guid sessionId, RunResult result, SessionStatus status, string? reply)
    {
        if (result.Outcome == RunOutcome.PausedForGate)
        {
            _streamHub.Publish(sessionId, ReplyStreamEvent.Confirmation(result.ApprovalId, result.GateCard));
        }

        if (!string.IsNullOrWhiteSpace(reply))
        {
            _streamHub.Publish(sessionId, ReplyStreamEvent.Text(reply));
        }

        _streamHub.Publish(
            sessionId,
            ReplyStreamEvent.Done(status.ToDbValue(), OutcomeToken(result.Outcome), reply));
    }

    private static SessionStatus MapStatus(RunOutcome outcome) => outcome switch
    {
        RunOutcome.PausedForGate => SessionStatus.AwaitingGate,
        RunOutcome.Escalated => SessionStatus.Escalated,
        RunOutcome.Degraded => SessionStatus.Degraded,
        _ => SessionStatus.Active
    };

    private static SessionStatus ParseStatus(string token)
        => EnumTokens.TryParseDbValue<SessionStatus>(token, out var status) ? status : SessionStatus.Active;

    /// <summary>Токен итога хода для API/SSE (paused_for_gate — snake_case, как в БД).</summary>
    private static string OutcomeToken(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Completed => "completed",
        RunOutcome.PausedForGate => "paused_for_gate",
        RunOutcome.Escalated => "escalated",
        RunOutcome.Degraded => "degraded",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "unknown run outcome")
    };
}
