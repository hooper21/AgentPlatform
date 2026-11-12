using AgentPlatform.Core.Enums;
using Microsoft.Agents.AI.Workflows;

namespace AgentPlatform.Runtime.Orchestration.Executors;

/// <summary>
/// Узел вызова read-тула. Исполняет вызов только если Policy Engine не требует
/// подтверждения: повторная серверная проверка здесь — защита от того, что
/// маршрут графа обойдёт гейт. После вызова возвращает управление LLM-ходу.
/// </summary>
internal sealed class ToolExecutor(OrchestrationState run)
    : Executor<OrchestrationMessage, OrchestrationMessage>("tool")
{
    /// <inheritdoc />
    public override async ValueTask<OrchestrationMessage> HandleAsync(
        OrchestrationMessage message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var toolName = message.ToolName
            ?? throw new InvalidOperationException("Tool name is required for the tool node.");

        var argumentsJson = message.ArgumentsJson ?? "{}";
        var tool = run.Agent.FindTool(toolName);

        if (tool is null)
        {
            // Модель запросила тул, которого нет в закреплённой версии агента.
            run.AppendMessage(MessageRole.Tool, $"tool_error {toolName}: инструмент не подключён к агенту");
            run.UpdateCounters(counters => counters with { ToolCalls = counters.ToolCalls + 1 });
            await run.SaveCheckpointAsync(cancellationToken).ConfigureAwait(false);

            return new OrchestrationMessage(OrchestrationSignal.LlmTurn);
        }

        var requirement = run.EvaluateGate(tool, argumentsJson);
        if (requirement.RequiresConfirmation)
        {
            // Policy Engine требует гейт: узел не имеет права исполнять вызов.
            await run.SaveCheckpointAsync(cancellationToken).ConfigureAwait(false);
            return new OrchestrationMessage(OrchestrationSignal.GateRequired, tool.ToolName, argumentsJson);
        }

        await run.InvokeToolAsync(tool, argumentsJson, cancellationToken).ConfigureAwait(false);
        await run.SaveCheckpointAsync(cancellationToken).ConfigureAwait(false);

        return new OrchestrationMessage(OrchestrationSignal.LlmTurn);
    }
}
