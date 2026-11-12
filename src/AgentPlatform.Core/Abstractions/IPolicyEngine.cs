using AgentPlatform.Core.Enums;

namespace AgentPlatform.Core.Abstractions;

/// <summary>
/// Policy Engine: серверная проверка политик тулов и необходимости HITL-гейта.
/// Политики не определяются промптом.
/// </summary>
public interface IPolicyEngine
{
    /// <summary>
    /// Возвращает действующую политику. Повышение политики запрещено
    /// (результат никогда не выше манифестной), понижение разрешено.
    /// </summary>
    ToolPolicy ResolveEffectivePolicy(ToolPolicy manifestPolicy, ToolPolicy? tenantOverride);

    /// <summary>
    /// Определяет, требуется ли подтверждение перед вызовом тула.
    /// </summary>
    GateEvaluation EvaluateGate(ToolPolicy effectivePolicy, GateMode configuredMode, bool predicateMatched);
}

/// <summary>Результат оценки гейта.</summary>
/// <param name="RequiresApproval">Требуется ли подтверждение.</param>
/// <param name="Reason">Причина решения (для аудита и gate_payload).</param>
public sealed record GateEvaluation(bool RequiresApproval, string Reason);
