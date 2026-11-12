using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Dto;

namespace AgentPlatform.Core.Abstractions;

/// <summary>
/// Заменяемый модуль оркестрации агента. Текущая реализация — MafOrchestrationRuntime
/// (AgentPlatform.Runtime). Смена оркестратора не мигрирует историю сессий: состояние
/// хранится только в нейтральном контракте <see cref="StateContract"/>.
/// </summary>
public interface IOrchestrationRuntime
{
    /// <summary>
    /// Обрабатывает входящее сообщение и продвигает граф диалога до завершения хода,
    /// паузы на гейте или эскалации.
    /// </summary>
    /// <param name="session">Контекст сессии (tenant, агент с version pinning, канал).</param>
    /// <param name="msg">Нормализованное входящее сообщение визитера.</param>
    /// <param name="ct">Отмена обработки (graceful shutdown).</param>
    Task<RunResult> RunAsync(SessionContext session, IncomingMessage msg, CancellationToken ct);

    /// <summary>
    /// Ставит сессию на паузу у HITL-гейта: сохраняет чекпоинт в нейтральном
    /// формате, заявка approvals создаётся до вызова (payload содержит её данные).
    /// Ожидающая сессия не удерживает поток/воркер.
    /// </summary>
    Task PauseAsync(SessionContext session, GatePayload payload);

    /// <summary>
    /// Возобновляет сессию с чекпоинта после решения по гейту
    /// (approved | rejected | expired | superseded).
    /// </summary>
    Task ResumeAsync(SessionContext session, Decision decision);
}
