using AgentPlatform.Core.Enums;
using Microsoft.Agents.AI.Workflows;

namespace AgentPlatform.Runtime.Orchestration.Executors;

/// <summary>
/// Вход графа: маршрутизирует сообщение хода по условным рёбрам —
/// <c>LlmTurn</c> в DialogExecutor (новый ход), <c>GateDecision</c> в GateExecutor (resume).
/// На новом ходе до вызова LLM подгружает RAG-контекст по сообщению пользователя;
/// ошибка/пустой результат знаний не прерывает диалог. Состояние сессии не меняет,
/// поэтому чекпоинт не пишет.
/// </summary>
internal sealed class TurnStartExecutor(OrchestrationState run)
    : Executor<OrchestrationMessage, OrchestrationMessage>("turn-start")
{
    /// <inheritdoc />
    public override async ValueTask<OrchestrationMessage> HandleAsync(
        OrchestrationMessage message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.Signal == OrchestrationSignal.LlmTurn)
        {
            var query = run.State.Messages
                .LastOrDefault(candidate => candidate.Role == MessageRole.User)
                ?.Content;

            await run.LoadKnowledgeContextAsync(query, cancellationToken).ConfigureAwait(false);
        }

        return message;
    }
}

