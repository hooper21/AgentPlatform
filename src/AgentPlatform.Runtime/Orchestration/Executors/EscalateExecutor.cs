using AgentPlatform.Core.Enums;
using Microsoft.Agents.AI.Workflows;

namespace AgentPlatform.Runtime.Orchestration.Executors;

/// <summary>
/// Узел эскалации: фиксирует передачу диалога оператору и ведёт граф в End.
/// Эскалация — не ошибка: итог хода <see cref="RunOutcome.Escalated"/>.
/// </summary>
internal sealed class EscalateExecutor(OrchestrationState run)
    : Executor<OrchestrationMessage, OrchestrationMessage>("escalate")
{
    /// <inheritdoc />
    public override async ValueTask<OrchestrationMessage> HandleAsync(
        OrchestrationMessage message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var reason = string.IsNullOrWhiteSpace(message.Note) ? "требуется помощь оператора" : message.Note;
        var reply = $"Передаю диалог оператору. Причина: {reason}.";

        run.AppendMessage(MessageRole.Assistant, reply);
        run.MarkEscalated(reply);
        await run.SaveCheckpointAsync(cancellationToken).ConfigureAwait(false);

        return new OrchestrationMessage(OrchestrationSignal.Completed);
    }
}
