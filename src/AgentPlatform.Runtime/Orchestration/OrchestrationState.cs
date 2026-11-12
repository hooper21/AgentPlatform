using System.Diagnostics;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;
using AgentPlatform.Runtime.Policy;

namespace AgentPlatform.Runtime.Orchestration;

/// <summary>
/// Состояние одного хода графа: нейтральное состояние сессии (<see cref="SessionState"/>),
/// закреплённая версия агента и накопленный итог хода.
/// <para>
/// После каждого суперстепа состояние сериализуется через <see cref="StateContract"/>
/// и пишется в checkpoints. Нативные чекпоинты MAF не используются.
/// </para>
/// </summary>
internal sealed class OrchestrationState
{
    private readonly ICheckpointStore _checkpointStore;
    private readonly IToolInvoker _toolInvoker;
    private readonly PolicyEngine _policyEngine;
    private readonly AgentRuntimeOptions _options;
    private readonly string _graphSignatureHash;
    private readonly int _superstepBudget;

    private OrchestrationState(
        SessionContext session,
        PlatformAgent agent,
        SessionState state,
        int superstep,
        ICheckpointStore checkpointStore,
        IToolInvoker toolInvoker,
        PolicyEngine policyEngine,
        AgentRuntimeOptions options,
        string graphSignatureHash)
    {
        Session = session;
        Agent = agent;
        State = state;
        Superstep = superstep;
        _checkpointStore = checkpointStore;
        _toolInvoker = toolInvoker;
        _policyEngine = policyEngine;
        _options = options;
        _graphSignatureHash = graphSignatureHash;
        _superstepBudget = superstep + options.MaxSuperstepsPerTurn;
        TenantPolicy = new TenantPolicyContext(session.TenantId);
    }

    /// <summary>Контекст сессии (tenant, агент, закреплённая версия, канал).</summary>
    public SessionContext Session { get; }

    /// <summary>Собранный агент закреплённой версии.</summary>
    public PlatformAgent Agent { get; }

    /// <summary>Нейтральное состояние сессии (обновляется узлами графа).</summary>
    public SessionState State { get; private set; }

    /// <summary>Политика тенанта по умолчанию (самый строгий режим без tool_settings).</summary>
    public TenantPolicyContext TenantPolicy { get; }

    /// <summary>Номер последнего сохранённого суперстепа.</summary>
    public int Superstep { get; private set; }

    /// <summary>Итог хода.</summary>
    public RunOutcome Outcome { get; private set; } = RunOutcome.Completed;

    /// <summary>Ответ ассистента (если ход завершён/эскалирован).</summary>
    public string? Reply { get; private set; }

    /// <summary>Ожидающий гейт (если ход остановлен на GateExecutor).</summary>
    public GatePayload? PendingGate { get; private set; }

    /// <summary>Идентификатор заявки approvals.id для ожидающего гейта.</summary>
    public Guid? ApprovalId { get; private set; }

    /// <summary>true — граф дошёл до End.</summary>
    public bool Completed { get; private set; }

    /// <summary>Создаёт состояние хода: новый ход (RunAsync) или продолжение с чекпоинта.</summary>
    public static OrchestrationState Start(
        SessionContext session,
        PlatformAgent agent,
        SessionState state,
        int superstep,
        ICheckpointStore checkpointStore,
        IToolInvoker toolInvoker,
        PolicyEngine policyEngine,
        AgentRuntimeOptions options,
        string graphSignatureHash)
        => new(
            session,
            agent,
            state,
            superstep,
            checkpointStore,
            toolInvoker,
            policyEngine,
            options,
            graphSignatureHash);

    /// <summary>Добавляет сообщение в историю диалога.</summary>
    public void AppendMessage(
        MessageRole role,
        string content,
        IReadOnlyList<StateToolCall>? toolCalls = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("Checkpoint messages must not be empty.");
        }

        var message = new StateMessage(role, content, DateTimeOffset.UtcNow, toolCalls);
        State = State with { Messages = [.. State.Messages, message] };
    }

    /// <summary>Обновляет счётчики сессии.</summary>
    public void UpdateCounters(Func<SessionCounters, SessionCounters> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        State = State with { Counters = update(State.Counters) };
    }

    /// <summary>Фиксирует паузу: pending_gate в нейтральном состоянии + итог хода.</summary>
    public void MarkPaused(GatePayload payload, Guid approvalId)
    {
        ArgumentNullException.ThrowIfNull(payload);

        PendingGate = payload;
        ApprovalId = approvalId;
        Outcome = RunOutcome.PausedForGate;
        State = State with
        {
            PendingGate = new PendingGateState(approvalId, payload, DateTimeOffset.UtcNow + _options.GateExpiry)
        };
    }

    /// <summary>Снимает ожидающий гейт после решения.</summary>
    public void ClearPendingGate()
    {
        PendingGate = null;
        ApprovalId = null;
        State = State with { PendingGate = null };
    }

    /// <summary>Фиксирует успешное завершение хода.</summary>
    public void MarkCompleted(string? reply)
    {
        Reply = reply;
        Outcome = RunOutcome.Completed;
    }

    /// <summary>Фиксирует эскалацию оператору.</summary>
    public void MarkEscalated(string reply)
    {
        Reply = reply;
        Outcome = RunOutcome.Escalated;
    }

    /// <summary>Фиксирует деградацию (ответ модели не получен и т.п.).</summary>
    public void MarkDegraded(string reply)
    {
        Reply = reply;
        Outcome = RunOutcome.Degraded;
    }

    /// <summary>Отмечает, что граф дошёл до End.</summary>
    public void MarkFinished() => Completed = true;

    /// <summary>
    /// Вычисляет требование гейта для вызова тула (Policy Engine, с учётом аргументов).
    /// </summary>
    public GateRequirement EvaluateGate(AgentToolConfig tool, string argumentsJson)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var arguments = ToolJson.Parse(argumentsJson);
        return _policyEngine.EvaluateWithArguments(tool.Tool, TenantPolicy, Session, arguments);
    }

    /// <summary>
    /// Вызывает тул через Connector Host и записывает результат в историю
    /// (роль tool + счётчик вызовов + latency).
    /// </summary>
    public async Task<ToolInvocationResult> InvokeToolAsync(
        AgentToolConfig tool,
        string argumentsJson,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var startedAt = Stopwatch.GetTimestamp();

        var result = await _toolInvoker
            .InvokeAsync(
                new ToolInvocationRequest(
                    Session.TenantId,
                    Session.SessionId,
                    tool.InstallationId,
                    tool.ToolName,
                    argumentsJson),
                cancellationToken)
            .ConfigureAwait(false);

        var latencyMs = (int)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        var content = result.Succeeded
            ? (string.IsNullOrWhiteSpace(result.Content) ? $"tool {tool.ToolName}: ok" : result.Content)
            : $"tool_error {tool.ToolName}: {result.Error ?? result.Content}";

        AppendMessage(
            MessageRole.Tool,
            content,
            [new StateToolCall(tool.ToolName, argumentsJson, result.ResultRef, latencyMs)]);

        UpdateCounters(counters => counters with { ToolCalls = counters.ToolCalls + 1 });

        return result;
    }

    /// <summary>
    /// Пишет чекпоинт после суперстепа: state → StateContract (нейтральный JSON) → checkpoints.
    /// </summary>
    public async Task SaveCheckpointAsync(CancellationToken cancellationToken)
    {
        var next = Superstep + 1;
        if (next > _superstepBudget)
        {
            throw new InvalidOperationException(
                $"Orchestration graph exceeded {_options.MaxSuperstepsPerTurn} supersteps in one turn. " +
                "Check escalation rules and tool limits.");
        }

        Superstep = next;

        var json = StateContract.Serialize(State);

        await _checkpointStore
            .SaveAsync(
                new SessionCheckpoint(
                    Session.TenantId,
                    Session.SessionId,
                    Superstep,
                    json,
                    _graphSignatureHash,
                    DateTimeOffset.UtcNow),
                cancellationToken)
            .ConfigureAwait(false);
    }
}

