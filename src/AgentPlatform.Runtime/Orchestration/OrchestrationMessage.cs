using AgentPlatform.Core.Contracts;

namespace AgentPlatform.Runtime.Orchestration;

/// <summary>
/// Сигнал условного перехода графа. Граф общается одним типом сообщения,
/// маршрутизация — по значению сигнала (условные рёбра MAF).
/// </summary>
internal enum OrchestrationSignal
{
    /// <summary>Ход LLM (DialogExecutor).</summary>
    LlmTurn = 0,

    /// <summary>Вызов read-тула (ToolExecutor).</summary>
    ToolCall = 1,

    /// <summary>Вызов write/destructive-тула требует HITL-гейта (GateExecutor).</summary>
    GateRequired = 2,

    /// <summary>Решение по гейту — вход ResumeAsync (GateExecutor).</summary>
    GateDecision = 3,

    /// <summary>Эскалация оператору (EscalateExecutor).</summary>
    Escalation = 4,

    /// <summary>Ход завершён: ответ готов или пауза зафиксирована (End).</summary>
    Completed = 5
}

/// <summary>
/// Сообщение между узлами графа. Содержит только данные хода: состояние сессии
/// живёт в <see cref="OrchestrationState"/> и сохраняется через
/// <see cref="StateContract"/> (а не в нативных структурах MAF).
/// </summary>
/// <param name="Signal">Куда ведёт условный переход.</param>
/// <param name="ToolName">Имя тула для вызова/гейта.</param>
/// <param name="ArgumentsJson">JSON-аргументы вызова тула.</param>
/// <param name="Decision">Решение по гейту (resume).</param>
/// <param name="Note">Причина эскалации.</param>
internal sealed record OrchestrationMessage(
    OrchestrationSignal Signal,
    string? ToolName = null,
    string? ArgumentsJson = null,
    Decision? Decision = null,
    string? Note = null);
