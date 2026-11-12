using System.Text.Json.Nodes;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Json;
using Microsoft.Agents.AI.Workflows;

namespace AgentPlatform.Runtime.Orchestration.Executors;

/// <summary>
/// HITL-гейт и полный жизненный цикл заявки approvals. Два входа:
/// <list type="bullet">
/// <item><see cref="OrchestrationSignal.GateRequired"/> — вызов write/destructive-тула:
/// гейт создаёт заявку approvals (статус pending) с маскированными аргументами,
/// фиксирует паузу в чекпоинте и уходит в End. Поток/воркер не удерживаются:
/// решение может прийти позже, в том числе после рестарта сервиса;</item>
/// <item><see cref="OrchestrationSignal.GateDecision"/> — resume с решением:
/// approved исполняет тул и возвращает результат модели; rejected/expired/superseded
/// сообщают модели, что действие не выполнено, и диалог продолжается.</item>
/// </list>
/// </summary>
internal sealed class GateExecutor(OrchestrationState run)
    : Executor<OrchestrationMessage, OrchestrationMessage>("gate")
{
    /// <summary>
    /// Метка системного решения «подтверждение визитера недостаточно для destructive-тула».
    /// </summary>
    private const string OperatorRequiredComment = "operator confirmation required";

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

        // В заявку и карточку попадают только маскированные аргументы; полные аргументы
        // сохраняются в чекпоинте для исполнения после approved.
        var argsMasked = PiiMasker.Mask(ToolJson.Parse(argumentsJson)) ?? new JsonObject();
        var payload = new GatePayload(
            tool.ToolName,
            argsMasked,
            Describe(tool, requirement, ToolJson.ToJsonString(argsMasked)),
            run.Session.SessionId,
            DateTimeOffset.UtcNow);

        await run.PauseAtGateAsync(payload, argumentsJson, cancellationToken).ConfigureAwait(false);
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

        // Аргументы для исполнения берутся из чекпоинта: в payload/approvals они маскированы.
        var argumentsJson = pending.ArgumentsJson
            ?? ToolJson.ToJsonString(pending.Payload.ArgsMasked);

        var resolved = ResolveDecision(pending, decision);

        if (resolved.Status == ApprovalStatus.Approved)
        {
            var requirement = run.EvaluateGate(tool, argumentsJson);

            if (requirement.Kind == GateRequirementKind.OperatorConfirmation
                && resolved.DecidedByKind != ApprovalDecisionKind.Operator)
            {
                // Destructive-тул подтверждает только оператор: подтверждение визитера не даёт прав.
                resolved = new Decision(
                    ApprovalStatus.Rejected,
                    ApprovalDecisionKind.System,
                    Comment: OperatorRequiredComment);
            }
        }

        // Решение фиксируется в approvals до исполнения тула (аудит раньше действия).
        await run.DecideApprovalAsync(resolved, cancellationToken).ConfigureAwait(false);

        if (resolved.Status == ApprovalStatus.Approved)
        {
            await run.InvokeToolAsync(tool, argumentsJson, cancellationToken).ConfigureAwait(false);
            run.ClearPendingGate();
        }
        else
        {
            run.AppendMessage(MessageRole.System, DescribeRejection(tool.ToolName, resolved));
            run.ClearPendingGate();
        }

        await run.SaveCheckpointAsync(cancellationToken).ConfigureAwait(false);

        // Граф продолжается: модель получает результат тула или отказ и отвечает.
        return new OrchestrationMessage(OrchestrationSignal.LlmTurn);
    }

    /// <summary>
    /// Применяет таймаут тенанта: approved/rejected, пришедшие после expires_at,
    /// трактуются как expired — действие не исполняется, диалог продолжается.
    /// </summary>
    private static Decision ResolveDecision(PendingGateState pending, Decision decision)
    {
        if (decision.Status is not (ApprovalStatus.Approved or ApprovalStatus.Rejected))
        {
            return decision;
        }

        if (pending.ExpiresAt is { } expiresAt && DateTimeOffset.UtcNow >= expiresAt)
        {
            return new Decision(
                ApprovalStatus.Expired,
                ApprovalDecisionKind.System,
                Comment: "gate timeout",
                DecidedAt: DateTimeOffset.UtcNow);
        }

        return decision;
    }

    private static string DescribeRejection(string toolName, Decision decision) => decision.Status switch
    {
        ApprovalStatus.Rejected when decision.Comment == OperatorRequiredComment =>
            $"Требуется подтверждение оператора для {toolName}: подтверждение визитера недостаточно.",
        ApprovalStatus.Rejected => $"Действие {toolName} отклонено ({decision.DecidedByKind.ToDbValue()}).",
        ApprovalStatus.Expired => $"Подтверждение {toolName} истекло, действие не выполнено.",
        ApprovalStatus.Superseded => $"Запрос подтверждения {toolName} заменён новым, действие не выполнено.",
        _ => $"Действие {toolName} не выполнено."
    };

    private static string Describe(AgentToolConfig tool, GateRequirement requirement, string maskedArgumentsJson)
    {
        var prefix = requirement.Kind == GateRequirementKind.OperatorConfirmation
            ? "Требуется подтверждение оператора"
            : "Требуется подтверждение";

        return $"{prefix}: {tool.ToolName}. Аргументы: {maskedArgumentsJson}";
    }
}

