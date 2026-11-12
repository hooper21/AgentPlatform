using System.Text.Json.Nodes;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Policy;

namespace AgentPlatform.Runtime.Policy;

/// <summary>
/// Серверный Policy Engine. Политика тула берётся из tool_settings тенанта, но не выше
/// манифестной: Runtime повторно проверяет понижение/повышение при каждом вызове —
/// некорректный (повышающий) конфиг не даёт агенту прав. Решение о гейте не зависит
/// от промпта агента.
/// <para>
/// Conditional-предикат применяется к аргументам вызова: <see cref="GetGateRequirement"/>
/// возвращает <see cref="GateRequirementKind.Conditional"/>, а
/// <see cref="EvaluateWithArguments"/> вычисляет предикат и возвращает итоговое
/// требование (None | UserConfirmation | OperatorConfirmation). Некорректный предикат
/// или аргументы трактуются как срабатывание гейта (fail-closed).
/// </para>
/// </summary>
public sealed class PolicyEngine : IPolicyEngine
{
    /// <inheritdoc />
    /// <exception cref="PolicyEscalationException">
    /// tool_settings тенанта повышает политику манифеста (проверка Runtime).
    /// </exception>
    public GateRequirement GetGateRequirement(
        ToolContext tool,
        TenantPolicyContext tenant,
        SessionContext session)
        => Resolve(tool, tenant, session).Requirement;

    /// <summary>
    /// Вычисляет требование гейта с учётом аргументов вызова: для режима conditional
    /// предикат применяется к аргументам тула. Возвращает уже разрешённое требование
    /// (<see cref="GateRequirementKind.None"/> | <see cref="GateRequirementKind.UserConfirmation"/> |
    /// <see cref="GateRequirementKind.OperatorConfirmation"/>).
    /// </summary>
    /// <param name="tool">Тул и его настройки тенанта.</param>
    /// <param name="tenant">Настройки тенанта (дефолтный режим гейта).</param>
    /// <param name="session">Контекст сессии.</param>
    /// <param name="arguments">JSON-аргументы вызова тула (могут быть null).</param>
    public GateRequirement EvaluateWithArguments(
        ToolContext tool,
        TenantPolicyContext tenant,
        SessionContext session,
        JsonNode? arguments)
    {
        var resolved = Resolve(tool, tenant, session);
        var requirement = resolved.Requirement;

        if (requirement.Kind != GateRequirementKind.Conditional)
        {
            return requirement;
        }

        if (requirement.Predicate is not { } predicate)
        {
            // Conditional без предиката не создаётся фабрикой; страховка — строгий режим.
            return PolicyGatePlanner.GetGateRequirement(resolved.EffectivePolicy, GateMode.Always);
        }

        bool predicateSatisfied;
        try
        {
            predicateSatisfied = GatePredicateEvaluator.Evaluate(predicate, arguments);
        }
        catch (GatePredicateException)
        {
            // Ошибка предиката/аргументов не снимает подтверждение.
            predicateSatisfied = true;
        }

        return PolicyGatePlanner.ResolveConditional(resolved.EffectivePolicy, predicateSatisfied);
    }

    /// <summary>
    /// Действующая политика тула после проверки понижения (без вычисления гейта).
    /// </summary>
    /// <exception cref="PolicyEscalationException">Политика тенанта выше манифестной.</exception>
    public ToolPolicy ResolveEffectivePolicy(ToolContext tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        return ToolPolicyRules.ResolveEffectivePolicy(tool.ManifestPolicy, tool.TenantPolicy, tool.ToolName);
    }

    private static ResolvedGate Resolve(ToolContext tool, TenantPolicyContext tenant, SessionContext session)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(session);

        if (tenant.TenantId == Guid.Empty)
        {
            throw new ArgumentException("tenant_id is required", nameof(tenant));
        }

        if (tenant.TenantId != session.TenantId)
        {
            throw new ArgumentException(
                "session.tenant_id must match the tenant policy context",
                nameof(session));
        }

        var effectivePolicy = ToolPolicyRules.ResolveEffectivePolicy(
            tool.ManifestPolicy,
            tool.TenantPolicy,
            tool.ToolName);

        var gateMode = tool.TenantGateMode ?? tenant.DefaultGateMode;

        return new ResolvedGate(
            PolicyGatePlanner.GetGateRequirement(effectivePolicy, gateMode, tool.ConditionalPredicate),
            effectivePolicy);
    }

    private sealed record ResolvedGate(GateRequirement Requirement, ToolPolicy EffectivePolicy);
}
