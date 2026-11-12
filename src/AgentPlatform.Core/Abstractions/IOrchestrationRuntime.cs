using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Dto;

namespace AgentPlatform.Core.Abstractions;

/// <summary>
/// Заменяемый модуль оркестрации агента. Текущая реализация — MafOrchestrationRuntime
/// (AgentPlatform.Runtime). Смена оркестратора не должна мигрировать историю сессий,
/// поэтому state хранится в нейтральном контракте <see cref="SessionStateContract"/>.
/// </summary>
public interface IOrchestrationRuntime
{
    /// <summary>Запускает обработку входящего сообщения и продвигает граф.</summary>
    Task<RunResult> RunAsync(SessionRef session, string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Переводит сессию в состояние ожидания подтверждения (HITL-гейт)
    /// и сохраняет чекпоинт.
    /// </summary>
    Task<RunResult> PauseAsync(SessionRef session, GatePayload gatePayload, CancellationToken cancellationToken = default);

    /// <summary>Возобновляет сессию после решения по гейту.</summary>
    Task<RunResult> ResumeAsync(SessionRef session, GateDecision decision, CancellationToken cancellationToken = default);
}
