using AgentPlatform.Runtime.Orchestration.Executors;
using Microsoft.Agents.AI.Workflows;

namespace AgentPlatform.Runtime.Orchestration;

/// <summary>
/// Топология графа на Microsoft.Agents.AI.Workflows:
/// <code>
/// turn-start --LlmTurn--> dialog --ToolCall--> tool --LlmTurn--> dialog
///      |                     |  \--GateRequired--> gate --LlmTurn--> dialog
///      |                     |                     gate --Completed--> end
///      |                     |--GateRequired--> gate
///      |                     |--Escalation--> escalate --Completed--> end
///      |                     \--Completed--> end
///      \--GateDecision--> gate (resume после решения по HITL-гейту)
/// </code>
/// Условный переход реализован типизированными рёбрами MAF по сигналу сообщения.
/// Состояние сессии при этом живёт в <see cref="OrchestrationState"/> и сохраняется
/// нейтральным JSON-контрактом после каждого суперстепа.
/// </summary>
internal static class OrchestrationGraph
{
    /// <summary>Имя графа (диагностика и трейсы).</summary>
    public const string Name = "agentplatform.dialog";

    /// <summary>Собирает граф для одного хода сессии.</summary>
    public static Workflow Build(OrchestrationState run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var start = new TurnStartExecutor(run);
        var dialog = new DialogExecutor(run);
        var tool = new ToolExecutor(run);
        var gate = new GateExecutor(run);
        var escalate = new EscalateExecutor(run);
        var end = new EndExecutor(run);

        var builder = new WorkflowBuilder(start);
        builder.WithName(Name);
        builder.WithDescription("LLM (dialog) -> tool (read) | gate (write/destructive) | escalate -> end");


        builder.AddEdge<OrchestrationMessage>(
            start, dialog, message => message!.Signal == OrchestrationSignal.LlmTurn);
        builder.AddEdge<OrchestrationMessage>(
            start, gate, message => message!.Signal == OrchestrationSignal.GateDecision);

        builder.AddEdge<OrchestrationMessage>(
            dialog, tool, message => message!.Signal == OrchestrationSignal.ToolCall);
        builder.AddEdge<OrchestrationMessage>(
            dialog, gate, message => message!.Signal == OrchestrationSignal.GateRequired);
        builder.AddEdge<OrchestrationMessage>(
            dialog, escalate, message => message!.Signal == OrchestrationSignal.Escalation);
        builder.AddEdge<OrchestrationMessage>(
            dialog, end, message => message!.Signal == OrchestrationSignal.Completed);

        builder.AddEdge<OrchestrationMessage>(
            tool, dialog, message => message!.Signal == OrchestrationSignal.LlmTurn);
        builder.AddEdge<OrchestrationMessage>(
            tool, gate, message => message!.Signal == OrchestrationSignal.GateRequired);

        builder.AddEdge<OrchestrationMessage>(
            gate, dialog, message => message!.Signal == OrchestrationSignal.LlmTurn);
        builder.AddEdge<OrchestrationMessage>(
            gate, end, message => message!.Signal == OrchestrationSignal.Completed);

        builder.AddEdge<OrchestrationMessage>(
            escalate, end, message => message!.Signal == OrchestrationSignal.Completed);

        return builder.Build();
    }
}
