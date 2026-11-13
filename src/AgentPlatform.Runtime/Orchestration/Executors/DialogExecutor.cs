using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Enums;
using AgentPlatform.Runtime.Knowledge;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace AgentPlatform.Runtime.Orchestration.Executors;

/// <summary>
/// Узел LLM-хода. Вызывает модель закреплённой версии агента с историей диалога и
/// декларациями активных тулов и решает, куда идёт условный переход:
/// <list type="bullet">
/// <item>ответ без вызова тула → End (Completed);</item>
/// <item>вызов read-тула (гейт не требуется) → ToolExecutor (ToolCall);</item>
/// <item>вызов write/destructive-тула → GateExecutor (GateRequired);</item>
/// <item>превышен лимит/ошибки модели → EscalateExecutor (Escalation).</item>
/// </list>
/// </summary>
internal sealed class DialogExecutor(OrchestrationState run)
    : Executor<OrchestrationMessage, OrchestrationMessage>("dialog")
{
    /// <inheritdoc />
    public override async ValueTask<OrchestrationMessage> HandleAsync(
        OrchestrationMessage message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var limitViolation = FindLimitViolation();
        if (limitViolation is not null)
        {
            return new OrchestrationMessage(OrchestrationSignal.Escalation, Note: limitViolation);
        }

        ChatResponse response;

        try
        {
            response = await CallModelAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return await HandleFailureAsync(exception, cancellationToken).ConfigureAwait(false);
        }

        return await HandleResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private Task<ChatResponse> CallModelAsync(CancellationToken cancellationToken)
    {
        var options = new ChatOptions
        {
            // Тулы передаются только декларациями: исполняет их граф (tool/gate), не LLM-клиент.
            Tools = run.Agent.ToolDeclarations.Cast<AITool>().ToList(),
            ToolMode = ChatToolMode.Auto
        };

        return run.Agent.ChatClient.GetResponseAsync(BuildMessages(), options, cancellationToken);
    }

    private async Task<OrchestrationMessage> HandleFailureAsync(
        Exception exception,
        CancellationToken cancellationToken)
    {
        run.UpdateCounters(counters => counters with { FailedTurns = counters.FailedTurns + 1 });
        run.AppendMessage(MessageRole.System, $"Ошибка LLM-хода: {exception.GetType().Name}");

        if (run.State.Counters.FailedTurns >= run.Agent.Config.EscalationRules.FailedTurnsThreshold)
        {
            return new OrchestrationMessage(OrchestrationSignal.Escalation, Note: "LLM недоступна");
        }

        const string degraded = "Не удалось получить ответ модели, попробуйте позже.";
        run.AppendMessage(MessageRole.Assistant, degraded);
        run.MarkDegraded(degraded);
        await run.SaveCheckpointAsync(cancellationToken).ConfigureAwait(false);

        return new OrchestrationMessage(OrchestrationSignal.Completed);
    }
    private async Task<OrchestrationMessage> HandleResponseAsync(
        ChatResponse response,
        CancellationToken cancellationToken)
    {
        var toolCall = response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .FirstOrDefault();

        run.UpdateCounters(counters => counters with
        {
            Turns = counters.Turns + 1,
            TokensIn = counters.TokensIn + (response.Usage?.InputTokenCount ?? 0),
            TokensOut = counters.TokensOut + (response.Usage?.OutputTokenCount ?? 0)
        });

        if (toolCall is null)
        {
            var text = response.Text?.Trim();
            var reply = string.IsNullOrWhiteSpace(text)
                ? "Не удалось сформулировать ответ, попробуйте переформулировать вопрос."
                : text;

            run.AppendMessage(MessageRole.Assistant, reply);
            run.MarkCompleted(reply);
            await run.SaveCheckpointAsync(cancellationToken).ConfigureAwait(false);

            return new OrchestrationMessage(OrchestrationSignal.Completed);
        }

        var argumentsJson = ToolJson.FromArguments(toolCall.Arguments);
        var textContent = response.Text?.Trim();

        run.AppendMessage(
            MessageRole.Assistant,
            string.IsNullOrWhiteSpace(textContent) ? $"Вызываю тул {toolCall.Name}" : textContent,
            [new StateToolCall(toolCall.Name, argumentsJson)]);

        if (run.State.Counters.ToolCalls >= run.Agent.Config.Limits.MaxToolCalls)
        {
            await run.SaveCheckpointAsync(cancellationToken).ConfigureAwait(false);
            return new OrchestrationMessage(
                OrchestrationSignal.Escalation,
                Note: $"Достигнут лимит вызовов тулов ({run.Agent.Config.Limits.MaxToolCalls})");
        }

        var tool = run.Agent.FindTool(toolCall.Name);
        if (tool is null)
        {
            // Тул не подключён к агенту: ToolExecutor отклонит вызов и вернёт ошибку модели.
            await run.SaveCheckpointAsync(cancellationToken).ConfigureAwait(false);
            return new OrchestrationMessage(OrchestrationSignal.ToolCall, toolCall.Name, argumentsJson);
        }

        var requirement = run.EvaluateGate(tool, argumentsJson);
        await run.SaveCheckpointAsync(cancellationToken).ConfigureAwait(false);

        return requirement.RequiresConfirmation
            ? new OrchestrationMessage(OrchestrationSignal.GateRequired, tool.ToolName, argumentsJson)
            : new OrchestrationMessage(OrchestrationSignal.ToolCall, tool.ToolName, argumentsJson);
    }

    private List<ChatMessage> BuildMessages()
    {
        var messages = new List<ChatMessage>();

        if (!string.IsNullOrWhiteSpace(run.Agent.Config.SystemPrompt))
        {
            // Системный промпт агента передаётся БЕЗ ИЗМЕНЕНИЙ (контракт платформы).
            messages.Add(new ChatMessage(ChatRole.System, run.Agent.Config.SystemPrompt));
        }

        // RAG-контекст — отдельное system-сообщение с разделителями «не инструкции»
        // и пометкой приоритета живых данных (результат тула главнее знаний).
        var knowledgeMessage = KnowledgePromptFormatter.BuildSystemMessage(run.Knowledge);
        if (knowledgeMessage is not null)
        {
            messages.Add(new ChatMessage(ChatRole.System, knowledgeMessage));
        }

        foreach (var message in run.State.Messages)
        {
            messages.Add(ToChatMessage(message));
        }

        return messages;
    }

    private static ChatMessage ToChatMessage(StateMessage message) => message.Role switch
    {
        MessageRole.User => new ChatMessage(ChatRole.User, message.Content),
        MessageRole.Assistant => new ChatMessage(ChatRole.Assistant, message.Content),
        MessageRole.Tool => new ChatMessage(ChatRole.Tool, message.Content),
        MessageRole.System => new ChatMessage(ChatRole.System, message.Content),
        MessageRole.Operator => new ChatMessage(ChatRole.Assistant, $"[оператор] {message.Content}"),
        _ => throw new InvalidOperationException($"Unknown message role {(int)message.Role} in checkpoint.")
    };

    private string? FindLimitViolation()
    {
        var limits = run.Agent.Config.Limits;

        if (run.State.Counters.Turns >= limits.MaxTurns)
        {
            return $"Достигнут лимит ходов агента ({limits.MaxTurns})";
        }

        if (run.State.Counters.FailedTurns >= run.Agent.Config.EscalationRules.FailedTurnsThreshold)
        {
            return "Слишком много неудачных ходов";
        }

        if (limits.MaxTokens > 0 && run.State.Counters.TokensIn + run.State.Counters.TokensOut >= limits.MaxTokens)
        {
            return $"Достигнут лимит токенов ({limits.MaxTokens})";
        }

        return null;
    }
}


