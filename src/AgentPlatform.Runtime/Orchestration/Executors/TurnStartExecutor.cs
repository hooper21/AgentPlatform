using Microsoft.Agents.AI.Workflows;

namespace AgentPlatform.Runtime.Orchestration.Executors;

/// <summary>
/// Вход графа: маршрутизирует сообщение хода по условным рёбрам —
/// <c>LlmTurn</c> в DialogExecutor (новый ход), <c>GateDecision</c> в GateExecutor (resume).
/// Состояние не меняет, поэтому чекпоинт не пишет.
/// </summary>
internal sealed class TurnStartExecutor()
    : Executor<OrchestrationMessage, OrchestrationMessage>("turn-start")
{
    /// <inheritdoc />
    public override ValueTask<OrchestrationMessage> HandleAsync(
        OrchestrationMessage message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        return ValueTask.FromResult(message);
    }
}
