using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Enums;
using Microsoft.Agents.AI.Workflows;

namespace AgentPlatform.Runtime.Orchestration.Executors;

/// <summary>
/// HITL-гейт. Два входа:
/// <list type="bullet">
/// <item><see cref="OrchestrationSignal.GateRequired"/> — вызов write/destructive-тула:
/// гейт создаёт заявку (approval id) и фиксирует паузу в чекпоинте, граф идёт в End;</item>
/// <item><see cref="OrchestrationSignal.GateDecision"/> — resume с решением:
/// approved исполняет тул, rejected/expired/superseded — нет; в обоих случаях диалог
/// продолжается LLM-ходом (граф «оживает» с чекпоинта).</item>
/// </list>
/// </summary>
internal sealed class GateExecutor(OrchestrationState run)
    : Executor<OrchestrationMessage, OrchestrationMessage>("gate")
{
    /// <inheritdoc />
    public override async ValueTask<OrchestrationMessage> HandleAsync(
        OrchestrationMessage message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        return message.Signal switch
        {
            OrchestrationSignal.GateRequired => await PauseAsync(message, cancellationToken).ConfigureAwait(false),
            OrchestrationSignal.GateDecision => await ResumeAsync(message, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException(
                $"Gate node received unexpected signal '{message.Signal}'.")
        };
    }

    private async Task<OrchestrationMessage> PauseAsync(
        OrchestrationMessage message,
        CancellationToken cancellationToken)
    {
        var toolName = message.ToolName
            ?? throw new InvalidOperationException("Tool name is required for the gate node.");

        var tool = run.Agent.FindTool(toolName)
            ?? throw new InvalidOperationException(
                $"Tool '{toolName}' is not available in agent version {run.Agent.Config.Version}.");

        var argumentsJson = message.ArgumentsJson ?? "{}";
        var requirement = run.EvaluateGate(tool, argumentsJson);

        if (!requirement.RequiresConfirmation)
        {
            // Конфигурация изменилась между узлами: гейт больше не нужен, вызов безопасен.
            await run.InvokeToolAsync(tool, argumentsJson, cancellationToken).ConfigureAwait(false);
            await run.SaveCheckpointAsync(cancellationToken).ConfigureAwait(false);

            return new OrchestrationMessage(OrchestrationSignal.LlmTurn);
        }

        var approvalId = Guid.NewGuid();
        var payload = new GatePayload(
            tool.ToolName,
            ToolJson.Parse(argumentsJson),
            Describe(tool, requirement, argumentsJson),
            UndoHint: null);

        run.MarkPaused(payload, approvalId);
        run.AppendMessage(MessageRole.System, $"Ожидает подтверждения: {payload.HumanDescription}");
        await run.SaveCheckpointAsync(cancellationToken).ConfigureAwait(false);

        return new OrchestrationMessage(OrchestrationSignal.Completed);
    }

    private async Task<OrchestrationMessage> ResumeAsync(
        OrchestrationMessage message,
        CancellationToken cancellationToken)
    {
        var pending = run.State.PendingGate
            ?? throw new InvalidOperationException("Resume requires a pending gate in the checkpoint.");

        var decision = message.Decision
            ?? throw new InvalidOperationException("Gate decision is required to resume the graph.");

        var tool = run.Agent.FindTool(pending.Payload.Tool)
            ?? throw new InvalidOperationException(
                $"Pending tool '{pending.Payload.Tool}' is not available in agent version " +
                $"{run.Agent.Config.Version} (version pinning).");

        var argumentsJson = ToolJson.ToJsonString(pending.Payload.ArgsMasked);

        if (decision.Status == ApprovalStatus.Approved)
        {
            var requirement = run.EvaluateGate(tool, argumentsJson);

            if (requirement.Kind == GateRequirementKind.OperatorConfirmation
                && decision.DecidedByKind != ApprovalDecisionKind.Operator)
            {
                // Destructive-тул подтверждает только оператор: подтверждение визитера не даёт прав.
                run.AppendMessage(
                    MessageRole.System,
                    $"Требуется подтверждение оператора для {tool.ToolName}: подтверждение визитера недостаточно.");
                run.ClearPendingGate();
                await run.SaveCheckpointAsync(cancellationToken).ConfigureAwait(false);

                return new OrchestrationMessage(OrchestrationSignal.LlmTurn);
            }

            await run.InvokeToolAsync(tool, argumentsJson, cancellationToken).ConfigureAwait(false);
            run.ClearPendingGate();
        }
        else
        {
            run.AppendMessage(MessageRole.System, DescribeRejection(tool.ToolName, decision));
            run.ClearPendingGate();
        }

        await run.SaveCheckpointAsync(cancellationToken).ConfigureAwait(false);

        // Граф продолжается: модель получает результат тула или отказ и отвечает.
        return new OrchestrationMessage(OrchestrationSignal.LlmTurn);
    }

    private static string DescribeRejection(string toolName, Decision decision) => decision.Status switch
    {
        ApprovalStatus.Rejected => $"Действие {toolName} отклонено ({decision.DecidedByKind.ToDbValue()}).",
        ApprovalStatus.Expired => $"Подтверждение {toolName} истекло, действие не выполнено.",
        ApprovalStatus.Superseded => $"Запрос подтверждения {toolName} заменён новым, действие не выполнено.",
        _ => $"Действие {toolName} не выполнено."
    };

    private static string Describe(AgentToolConfig tool, GateRequirement requirement, string argumentsJson)
    {
        var prefix = requirement.Kind == GateRequirementKind.OperatorConfirmation
            ? "Требуется подтверждение оператора"
            : "Требуется подтверждение";

        return $"{prefix}: {tool.ToolName}. Аргументы: {argumentsJson}";
    }
}

