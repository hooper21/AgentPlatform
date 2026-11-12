using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Enums;

namespace AgentPlatform.Core.Policy;

/// <summary>
/// Преобразование действующей политики тула и режима гейта (tool_settings.gate_settings.mode)
/// в требование Policy Engine. Серверные правила:
/// <list type="bullet">
/// <item><c>always</c> — подтверждение требуется всегда; для read-тула это
/// <see cref="GateRequirement.UserConfirmation"/>, потому что режим строгий
/// («настроек нет» = самый строгий режим);</item>
/// <item><c>conditional</c> — требование <see cref="GateRequirement.Conditional"/>:
/// при вызове тула предикат вычисляется на аргументах; без валидного предиката
/// режим деградирует в <c>always</c> (fail-closed);</item>
/// <item><c>never</c> — гейт снимается только для read-политики; write и destructive
/// остаются под гейтом: конфигурация не может отключить серверную проверку,
/// для этого политику нужно понизить.</item>
/// </list>
/// </summary>
public static class PolicyGatePlanner
{
    /// <summary>
    /// Возвращает требование гейта для действующей политики и режима.
    /// </summary>
    /// <param name="effectivePolicy">Политика после проверки понижения.</param>
    /// <param name="gateMode">Режим гейта из tool_settings или дефолт тенанта.</param>
    /// <param name="predicate">Предикат conditional-режима (может быть null).</param>
    public static GateRequirement GetGateRequirement(
        ToolPolicy effectivePolicy,
        GateMode gateMode,
        GatePredicate? predicate = null)
    {
        if (!Enum.IsDefined(effectivePolicy))
        {
            throw new ArgumentException($"unknown tool policy value {(int)effectivePolicy}", nameof(effectivePolicy));
        }

        return gateMode switch
        {
            GateMode.Conditional when GatePredicateEvaluator.IsValid(predicate)
                => GateRequirement.Conditional(predicate!),
            GateMode.Conditional => Strictest(effectivePolicy),
            GateMode.Always => Strictest(effectivePolicy),
            GateMode.Never => Baseline(effectivePolicy),
            _ => throw new ArgumentException($"unknown gate mode value {(int)gateMode}", nameof(gateMode))
        };
    }

    /// <summary>
    /// Результат вычисления conditional-предиката на аргументах вызова:
    /// подтверждение не нужно, если предикат не выполнен. Кто подтверждает —
    /// определяет политика: destructive — оператор, read/write — визитер.
    /// </summary>
    public static GateRequirement ResolveConditional(ToolPolicy effectivePolicy, bool predicateSatisfied)
    {
        if (!predicateSatisfied)
        {
            return GateRequirement.None;
        }

        return effectivePolicy == ToolPolicy.Destructive
            ? GateRequirement.OperatorConfirmation
            : GateRequirement.UserConfirmation;
    }

    private static GateRequirement Strictest(ToolPolicy policy)
        => policy == ToolPolicy.Read ? GateRequirement.UserConfirmation : Baseline(policy);

    private static GateRequirement Baseline(ToolPolicy policy) => policy switch
    {
        ToolPolicy.Read => GateRequirement.None,
        ToolPolicy.Write => GateRequirement.UserConfirmation,
        ToolPolicy.Destructive => GateRequirement.OperatorConfirmation,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "unknown tool policy")
    };
}
