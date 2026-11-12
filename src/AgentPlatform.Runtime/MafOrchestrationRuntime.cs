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
    public Task<RunResult> RunAsync(SessionContext session, IncomingMessage msg, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(msg);
        ct.ThrowIfCancellationRequested();

        // TODO(2.x): собрать Workflow (WorkflowBuilder), выполнить шаг и вернуть чекпоинт.
        var result = new RunResult(
            RunOutcome.Completed,
            Message: $"[runtime stub] получено сообщение: {msg.Text}",
            GraphSignatureHash: GraphSignatureHash,
            Superstep: 1);

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task PauseAsync(SessionContext session, GatePayload payload)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(payload);

        // TODO(2.x): остановить граф на gate-узле и сохранить чекпоинт через StateContract.
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ResumeAsync(SessionContext session, Decision decision)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(decision);

        if (decision.Status == ApprovalStatus.Pending)
        {
            throw new ArgumentException(
                "Resume requires a terminal decision (approved | rejected | expired | superseded).",
                nameof(decision));
        }

        // TODO(2.x): проверить graph_signature_hash чекпоинта и продолжить граф с решением.
        return Task.CompletedTask;
    }
}
