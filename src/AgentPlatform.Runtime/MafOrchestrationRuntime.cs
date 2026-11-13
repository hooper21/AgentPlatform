using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Validation;
using AgentPlatform.Runtime.Orchestration;
using AgentPlatform.Runtime.Policy;
using Microsoft.Agents.AI.Workflows;

namespace AgentPlatform.Runtime;

/// <summary>
/// Оркестратор агента на Microsoft.Agents.AI.Workflows (Agent Framework 1.x).
/// <para>
/// Граф: DialogExecutor (LLM-ход) → условный переход → ToolExecutor (read) →
/// DialogExecutor | GateExecutor (write/destructive) | EscalateExecutor → End.
/// </para>
/// <para>
/// Состояние сессии хранится только в нейтральном контракте <see cref="StateContract"/>:
/// после каждого суперстепа state → checkpoints (не нативные чекпоинты MAF).
/// ResumeAsync восстанавливает состояние из чекпоинта, продолжает граф с gate-узла
/// и работает на версии агента, закреплённой за сессией (version pinning).
/// </para>
/// </summary>
public sealed class MafOrchestrationRuntime : IOrchestrationRuntime
{
    /// <summary>
    /// Хеш топологии графа. Чекпоинты валидируют resume по этому значению,
    /// поэтому смена графа обязана менять и подпись.
    /// </summary>
    public const string GraphSignatureHash = "agentplatform.maf.graph.v1";

    private readonly AgentFactory _agentFactory;
    private readonly ICheckpointStore _checkpointStore;
    private readonly IToolInvoker _toolInvoker;
    private readonly PolicyEngine _policyEngine;
    private readonly IApprovalStore _approvalStore;
    private readonly ITenantGateTimeoutProvider _gateTimeouts;
    private readonly AgentRuntimeOptions _options;
    private readonly IKnowledgeContextProvider? _knowledgeContextProvider;

    /// <summary>Создаёт оркестратор.</summary>
    /// <param name="agentFactory">Сборка агента закреплённой версии (AgentFactory).</param>
    /// <param name="checkpointStore">Хранилище чекпоинтов (нейтральный JSON).</param>
    /// <param name="toolInvoker">Вызов тулов коннекторов через Connector Host.</param>
    /// <param name="policyEngine">Серверная проверка политик и HITL-гейтов.</param>
    /// <param name="approvalStore">Заявки approvals (полный жизненный цикл HITL).</param>
    /// <param name="gateTimeouts">Таймаут ожидания решения по настройке тенанта.</param>
    /// <param name="options">Настройки Runtime (TTL кеша, таймауты, лимиты).</param>
    /// <param name="knowledgeContextProvider">RAG-порт Knowledge Service (null — без знаний).</param>
    public MafOrchestrationRuntime(
        AgentFactory agentFactory,
        ICheckpointStore checkpointStore,
        IToolInvoker toolInvoker,
        PolicyEngine policyEngine,
        IApprovalStore approvalStore,
        ITenantGateTimeoutProvider gateTimeouts,
        AgentRuntimeOptions options,
        IKnowledgeContextProvider? knowledgeContextProvider = null)
    {
        _agentFactory = agentFactory ?? throw new ArgumentNullException(nameof(agentFactory));
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        _toolInvoker = toolInvoker ?? throw new ArgumentNullException(nameof(toolInvoker));
        _policyEngine = policyEngine ?? throw new ArgumentNullException(nameof(policyEngine));
        _approvalStore = approvalStore ?? throw new ArgumentNullException(nameof(approvalStore));
        _gateTimeouts = gateTimeouts ?? throw new ArgumentNullException(nameof(gateTimeouts));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _knowledgeContextProvider = knowledgeContextProvider;
    }

    /// <inheritdoc />
    public async Task<RunResult> RunAsync(SessionContext session, IncomingMessage msg, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(msg);
        ct.ThrowIfCancellationRequested();

        if (msg.TenantId != session.TenantId)
        {
            throw new ArgumentException("message.tenant_id must match the session tenant", nameof(msg));
        }

        if (string.IsNullOrWhiteSpace(msg.Text))
        {
            throw new ArgumentException("message.text must not be empty", nameof(msg));
        }

        var checkpoint = await _checkpointStore
            .GetLatestAsync(session.TenantId, session.SessionId, ct)
            .ConfigureAwait(false);

        var state = RestoreState(checkpoint);

        if (state.PendingGate is { } pendingGate)
        {
            throw new InvalidOperationException(
                $"Session {session.SessionId} is awaiting a gate decision " +
                $"(approval {pendingGate.ApprovalId}); use ResumeAsync.");
        }

        var agent = await _agentFactory.CreateAsync(session, ct).ConfigureAwait(false);

        state = state with
        {
            Messages = [.. state.Messages, new StateMessage(MessageRole.User, msg.Text.Trim(), DateTimeOffset.UtcNow)]
        };

        var run = OrchestrationState.Start(
            session,
            agent,
            state,
            checkpoint?.Superstep ?? 0,
            _checkpointStore,
            _toolInvoker,
            _policyEngine,
            _approvalStore,
            _gateTimeouts,
            _options,
            GraphSignatureHash,
            _knowledgeContextProvider);

        await ExecuteAsync(run, new OrchestrationMessage(OrchestrationSignal.LlmTurn), ct).ConfigureAwait(false);

        return BuildResult(run);
    }

    /// <inheritdoc />
    public async Task PauseAsync(SessionContext session, GatePayload payload)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(payload);

        GatePayloadValidator.Validate(payload);

        if (payload.SessionId != Guid.Empty && payload.SessionId != session.SessionId)
        {
            throw new ArgumentException(
                "payload.session_id must match the paused session",
                nameof(payload));
        }

        var checkpoint = await _checkpointStore
            .GetLatestAsync(session.TenantId, session.SessionId, CancellationToken.None)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Session has no checkpoint: a pause at a HITL gate is produced by RunAsync " +
                "(gate node writes the pending gate and approval id).");

        var state = RestoreState(checkpoint);

        var pending = state.PendingGate
            ?? throw new InvalidOperationException(
                "Session is not awaiting a gate: PauseAsync expects a pending gate written by the gate node.");

        if (!string.Equals(pending.Payload.Tool, payload.Tool, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Another gate is already pending ({pending.Payload.Tool}); " +
                $"pause payload for '{payload.Tool}' cannot replace it.");
        }

        // Пауза и заявка уже созданы gate-узлом — операция идемпотентна. Если заявки
        // нет (например, чекпоинт старого образца), она восстанавливается здесь.
        var approval = await _approvalStore
            .FindAsync(session.TenantId, pending.ApprovalId, CancellationToken.None)
            .ConfigureAwait(false);

        if (approval is null)
        {
            await _approvalStore
                .CreateAsync(
                    new ApprovalRequest(
                        pending.ApprovalId,
                        session.TenantId,
                        session.SessionId,
                        pending.Payload,
                        ApprovalStatus.Pending,
                        pending.Payload.CreatedAt,
                        pending.ExpiresAt
                            ?? DateTimeOffset.UtcNow + _gateTimeouts.GetGateTimeout(session.TenantId)),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task ResumeAsync(SessionContext session, Decision decision)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(decision);

        if (decision.Status == ApprovalStatus.Pending)
        {
            throw new ArgumentException(
                "Resume requires a terminal decision (approved | rejected | expired | superseded).",
                nameof(decision));
        }

        var checkpoint = await _checkpointStore
            .GetLatestAsync(session.TenantId, session.SessionId, CancellationToken.None)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Session {session.SessionId} has no checkpoint to resume from.");

        var state = RestoreState(checkpoint);

        var pending = state.PendingGate
            ?? throw new InvalidOperationException(
                $"Session {session.SessionId} has no pending gate; ResumeAsync requires a paused session.");

        if (session.Status == SessionStatus.Closed)
        {
            // Сессию отменили, пока гейт ждал решения: заявка закрывается, тул не исполняется.
            await _approvalStore
                .CancelPendingAsync(
                    session.TenantId,
                    session.SessionId,
                    "session closed while awaiting gate",
                    CancellationToken.None)
                .ConfigureAwait(false);

            throw new InvalidOperationException(
                $"Session {session.SessionId} is closed: pending approval {pending.ApprovalId} " +
                "was superseded and the gated action is not executed.");
        }

        // Заявка живёт в БД, поэтому resume работает после рестарта сервиса.
        var approval = await _approvalStore
            .FindAsync(session.TenantId, pending.ApprovalId, CancellationToken.None)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Approval {pending.ApprovalId} of session {session.SessionId} is not found.");

        if (approval.SessionId != session.SessionId)
        {
            throw new InvalidOperationException(
                $"Approval {approval.Id} belongs to session {approval.SessionId}, not {session.SessionId}.");
        }

        if (approval.Status != ApprovalStatus.Pending)
        {
            throw new InvalidOperationException(
                $"Approval {approval.Id} is already {approval.Status}; the gate cannot be resumed twice.");
        }

        // Version pinning: агент собирается на версии, с которой начата сессия.
        var agent = await _agentFactory
            .CreateAsync(session, CancellationToken.None)
            .ConfigureAwait(false);

        if (agent.FindTool(pending.Payload.Tool) is null)
        {
            throw new InvalidOperationException(
                $"Pending tool '{pending.Payload.Tool}' is not available in the pinned " +
                $"agent version {session.AgentVersion}; resume is rejected.");
        }

        var run = OrchestrationState.Start(
            session,
            agent,
            state,
            checkpoint.Superstep,
            _checkpointStore,
            _toolInvoker,
            _policyEngine,
            _approvalStore,
            _gateTimeouts,
            _options,
            GraphSignatureHash,
            _knowledgeContextProvider);

        await ExecuteAsync(
                run,
                new OrchestrationMessage(OrchestrationSignal.GateDecision, Decision: decision),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(
        OrchestrationState run,
        OrchestrationMessage input,
        CancellationToken cancellationToken)
    {
        var workflow = OrchestrationGraph.Build(run);

        await using var execution = await InProcessExecution
            .RunAsync(workflow, input, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        foreach (var workflowEvent in execution.NewEvents)
        {
            if (workflowEvent is WorkflowErrorEvent error)
            {
                throw new InvalidOperationException($"Orchestration workflow failed: {error.Data}");
            }

            if (workflowEvent is ExecutorFailedEvent failed)
            {
                throw new InvalidOperationException(
                    $"Orchestration executor '{failed.ExecutorId}' failed: {failed.Data}");
            }
        }

        if (!run.Completed)
        {
            throw new InvalidOperationException("Orchestration graph finished without reaching the end node.");
        }
    }

    private static SessionState RestoreState(SessionCheckpoint? checkpoint)
    {
        if (checkpoint is null)
        {
            return new SessionState();
        }

        if (!string.Equals(checkpoint.GraphSignatureHash, GraphSignatureHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Checkpoint graph signature '{checkpoint.GraphSignatureHash}' does not match " +
                $"runtime graph '{GraphSignatureHash}': session cannot be resumed by this runtime.");
        }

        return StateContract.DeserializeRequired(checkpoint.State);
    }

    private static RunResult BuildResult(OrchestrationState run) => new(
        run.Outcome,
        run.Reply,
        run.PendingGate,
        run.ApprovalId,
        GraphSignatureHash,
        run.Superstep,
        run.GateCardJson);
}

