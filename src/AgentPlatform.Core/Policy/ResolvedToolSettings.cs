using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Enums;

namespace AgentPlatform.Core.Policy;

/// <summary>
/// Результат валидации конфигурации тула (tool_settings) и вычисленная действующая
/// политика. Используется Controller API при сохранении настроек и Runtime при
/// построении <see cref="ToolContext"/> — правила понижения/повышения одни и те же.
/// </summary>
/// <param name="ManifestPolicy">Политика из манифеста коннектора (потолок).</param>
/// <param name="EffectivePolicy">Действующая политика после проверки понижения.</param>
/// <param name="PolicySource">Источник политики: manifest | tenant_override.</param>
/// <param name="GateMode">Режим гейта из настроек (по умолчанию always).</param>
/// <param name="Predicate">Предикат режима conditional (если задан).</param>
public sealed record ResolvedToolSettings(
    ToolPolicy ManifestPolicy,
    ToolPolicy EffectivePolicy,
    PolicySource PolicySource,
    GateMode GateMode,
    GatePredicate? Predicate = null)
{
    /// <summary>Требование гейта, соответствующее валидированной конфигурации.</summary>
    public GateRequirement Requirement => PolicyGatePlanner.GetGateRequirement(EffectivePolicy, GateMode, Predicate);
}
