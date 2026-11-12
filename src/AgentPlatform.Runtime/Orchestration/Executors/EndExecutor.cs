using Microsoft.Agents.AI.Workflows;

namespace AgentPlatform.Runtime.Orchestration.Executors;

/// <summary>
/// Конец графа: отмечает, что ход дошёл до End. Чекпоинт уже записан узлом,
/// который завершил ход (dialog / gate / escalate).
/// </summary>
internal sealed class EndExecutor(OrchestrationState run)
    : Executor<OrchestrationMessage>("end")
{
    /// <inheritdoc />
    public override ValueTask HandleAsync(
        OrchestrationMessage message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        run.MarkFinished();

        return ValueTask.CompletedTask;
    }
}
