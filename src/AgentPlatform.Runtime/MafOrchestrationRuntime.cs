using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;

namespace AgentPlatform.Runtime;

/// <summary>
/// Заглушка оркестратора на Microsoft.Agents.AI.Workflows (Agent Framework 1.x).
/// <para>
/// Реальный граф (LLM-узел → tool-узел → gate-узел → чекпоинт) и resume
/// реализуются в задачах фазы 2. Контракт <see cref="IOrchestrationRuntime"/>
/// уже зафиксирован, поэтому замена реализации не затрагивает Gateway.
/// </para>
/// </summary>
public sealed class MafOrchestrationRuntime : IOrchestrationRuntime
{
    /// <summary>
    /// Хеш топологии графа. В v1 константа: чекпоинты валидируют resume по этому
    /// значению, поэтому смена графа обязана менять и подпись.
    /// </summary>
    public const string GraphSignatureHash = "agentplatform.maf.graph.v1";

    /// <inheritdoc />
    public Task<RunResult> RunAsync(
        SessionRef session,
        string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        // TODO(2.x): собрать Workflow (WorkflowBuilder), выполнить шаг и вернуть чекпоинт.
        var result = new RunResult(
            RunOutcome.Completed,
            Message: $"[runtime stub] получено сообщение: {message}",
            GraphSignatureHash: GraphSignatureHash,
            Superstep: 1);

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<RunResult> PauseAsync(
        SessionRef session,
        GatePayload gatePayload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(gatePayload);

        // TODO(2.x): остановить граф на gate-узле, сохранить чекпоинт и вернуть approval id.
        var result = new RunResult(
            RunOutcome.PausedForGate,
            Message: null,
            PendingGate: gatePayload,
            GraphSignatureHash: GraphSignatureHash,
            Superstep: 1);

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<RunResult> ResumeAsync(
        SessionRef session,
        GateDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(decision);

        // TODO(2.x): проверить graph_signature_hash чекпоинта и продолжить граф с решением оператора.
        var result = new RunResult(
            RunOutcome.Completed,
            Message: $"[runtime stub] решение по гейту: {decision.Status}",
            GraphSignatureHash: GraphSignatureHash,
            Superstep: 2);

        return Task.FromResult(result);
    }
}
