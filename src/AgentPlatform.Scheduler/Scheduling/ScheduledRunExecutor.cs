using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Json;
using AgentPlatform.Infrastructure.Persistence.Entities;
using AgentPlatform.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Scheduler.Scheduling;

/// <summary>
/// Один плановый запуск агента: создаёт сессию без входящего сообщения визитера,
/// пишет системный триггер «плановый запуск» в messages, выполняет ход через
/// <see cref="IOrchestrationRuntime.RunAsync"/> и сохраняет ответ агента.
/// <para>
/// Для сессий Scheduler используется служебный канал типа <c>system</c>:
/// sessions.channel_id обязателен, но плановый запуск не привязан к виджету
/// или Telegram. Канал создаётся один раз на тенант.
/// </para>
/// </summary>
public sealed class ScheduledRunExecutor(
    IScheduledRunStore runs,
    ITenantScopedRepository<Agent> agents,
    ITenantScopedRepository<Channel> channels,
    ITenantScopedRepository<AgentSession> sessions,
    ITenantScopedRepository<Message> messages,
    TenantContext tenantContext,
    IOrchestrationRuntime runtime,
    TimeProvider timeProvider,
    ILogger<ScheduledRunExecutor> logger)
{
    /// <summary>Тип служебного канала плановых запусков (channels.type).</summary>
    public const string SystemChannelType = "system";

    private readonly IScheduledRunStore _runs = runs ?? throw new ArgumentNullException(nameof(runs));
    private readonly ITenantScopedRepository<Agent> _agents = agents ?? throw new ArgumentNullException(nameof(agents));
    private readonly ITenantScopedRepository<Channel> _channels = channels ?? throw new ArgumentNullException(nameof(channels));
    private readonly ITenantScopedRepository<AgentSession> _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly ITenantScopedRepository<Message> _messages = messages ?? throw new ArgumentNullException(nameof(messages));
    private readonly TenantContext _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
    private readonly IOrchestrationRuntime _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<ScheduledRunExecutor> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Выполняет расписание. Ошибки конфигурации (агент удалён, LLM/граф упал)
    /// не останавливают worker: запуск помечается last_run_status = error.
    /// </summary>
    public async Task<ScheduledRunExecution> ExecuteAsync(
        ScheduledRunInfo run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        // Tenant-фильтрованные репозитории требуют установленный контекст.
        _tenantContext.SetTenant(run.TenantId);

        AgentSession? session = null;

        try
        {
            var agent = await _agents.FindAsync(run.AgentId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Agent {run.AgentId} is not found in tenant {run.TenantId}.");

            var channel = await ResolveSystemChannelAsync(cancellationToken).ConfigureAwait(false);
            var startedAt = _timeProvider.GetUtcNow();

            session = new AgentSession
            {
                AgentId = agent.Id,
                AgentVersion = agent.CurrentVersion,
                ChannelId = channel.Id,
                UserRef = $"scheduler:{run.Id:N}",
                Status = SessionStatus.Active.ToDbValue(),
                Meta = PlatformJson.SerializeDb(new ScheduledSessionMeta(ScheduledTrigger.MetadataValue, run.Id)),
                StartedAt = startedAt,
                LastActivityAt = startedAt
            };

            await _sessions.AddAsync(session, cancellationToken).ConfigureAwait(false);

            // Системный триггер виден в журнале диалога до вызова агента.
            await _messages
                .AddAsync(
                    new Message
                    {
                        SessionId = session.Id,
                        Role = MessageRole.System.ToDbValue(),
                        Content = ScheduledTrigger.Text,
                        Ts = startedAt
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            await _sessions.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var context = new SessionContext(
                session.Id,
                run.TenantId,
                agent.Id,
                agent.CurrentVersion,
                channel.Id,
                session.UserRef);

            var incoming = new IncomingMessage(
                run.TenantId,
                channel.Id,
                session.UserRef,
                ScheduledTrigger.Text,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ScheduledTrigger.MetadataKey] = ScheduledTrigger.MetadataValue,
                    [ScheduledTrigger.ScheduledRunIdMetadataKey] = run.Id.ToString("D")
                },
                startedAt);

            var result = await _runtime.RunAsync(context, incoming, cancellationToken).ConfigureAwait(false);

            var status = MapStatus(result.Outcome);
            session.Status = status.ToDbValue();
            session.LastActivityAt = _timeProvider.GetUtcNow();
            _sessions.Update(session);

            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                await _messages
                    .AddAsync(
                        new Message
                        {
                            SessionId = session.Id,
                            Role = MessageRole.Assistant.ToDbValue(),
                            Content = result.Message,
                            Ts = _timeProvider.GetUtcNow()
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await _sessions.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            // Degraded/Escalated — запуск состоялся, но требует внимания оператора.
            var runStatus = result.Outcome is RunOutcome.Completed or RunOutcome.PausedForGate
                ? ScheduledRunStatus.Ok
                : ScheduledRunStatus.Error;

            await _runs
                .MarkRunAsync(run.TenantId, run.Id, _timeProvider.GetUtcNow(), runStatus, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Scheduled run {ScheduledRunId} finished: session {SessionId}, outcome {Outcome}, status {Status}",
                run.Id,
                session.Id,
                result.Outcome,
                runStatus);

            return new ScheduledRunExecution(runStatus == ScheduledRunStatus.Ok, session.Id, result.Outcome, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Scheduled run {ScheduledRunId} failed (tenant {TenantId}, agent {AgentId})",
                run.Id,
                run.TenantId,
                run.AgentId);

            await TryMarkErrorAsync(run, cancellationToken).ConfigureAwait(false);

            return new ScheduledRunExecution(false, session?.Id, null, exception.Message);
        }
    }

    private async Task<Channel> ResolveSystemChannelAsync(CancellationToken cancellationToken)
    {
        var channel = await _channels.Query
            .OrderBy(candidate => candidate.CreatedAt)
            .FirstOrDefaultAsync(candidate => candidate.Type == SystemChannelType, cancellationToken)
            .ConfigureAwait(false);

        if (channel is not null)
        {
            return channel;
        }

        var now = _timeProvider.GetUtcNow();
        var created = new Channel
        {
            Type = SystemChannelType,
            Config = "{}",
            Status = "active",
            CreatedAt = now,
            UpdatedAt = now
        };

        await _channels.AddAsync(created, cancellationToken).ConfigureAwait(false);
        await _channels.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "System channel {ChannelId} created for scheduled runs (tenant {TenantId})",
            created.Id,
            _tenantContext.RequireTenantId());

        return created;
    }

    private async Task TryMarkErrorAsync(ScheduledRunInfo run, CancellationToken cancellationToken)
    {
        try
        {
            await _runs
                .MarkRunAsync(
                    run.TenantId,
                    run.Id,
                    _timeProvider.GetUtcNow(),
                    ScheduledRunStatus.Error,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                exception,
                "Failed to mark scheduled run {ScheduledRunId} as error",
                run.Id);
        }
    }

    private static SessionStatus MapStatus(RunOutcome outcome) => outcome switch
    {
        RunOutcome.PausedForGate => SessionStatus.AwaitingGate,
        RunOutcome.Escalated => SessionStatus.Escalated,
        RunOutcome.Degraded => SessionStatus.Degraded,
        _ => SessionStatus.Active
    };

    /// <summary>jsonb-контекст сессии: происхождение запуска.</summary>
    private sealed record ScheduledSessionMeta(string Trigger, Guid ScheduledRunId);
}

/// <summary>Итог одного планового запуска.</summary>
/// <param name="Succeeded">false — запуск завершился ошибкой.</param>
/// <param name="SessionId">Созданная сессия (null — сессия не успела создаться).</param>
/// <param name="Outcome">Итог хода агента (null — ход не выполнялся).</param>
/// <param name="Error">Сообщение об ошибке для лога/диагностики.</param>
public sealed record ScheduledRunExecution(
    bool Succeeded,
    Guid? SessionId,
    RunOutcome? Outcome,
    string? Error);
