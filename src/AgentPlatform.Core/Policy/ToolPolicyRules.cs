using AgentPlatform.Core.Enums;

namespace AgentPlatform.Core.Policy;

/// <summary>
/// Правила политик тулов. Политика манифеста — потолок: tool_settings тенанта может
/// только понизить политику (destructive → write → read), повышение запрещено.
/// Сравнение опирается на порядок значений <see cref="ToolPolicy"/>
/// (Read = 0, Write = 1, Destructive = 2).
/// </summary>
public static class ToolPolicyRules
{
    /// <summary>
    /// true, если политика тенанта выше манифестной (повышение запрещено).
    /// </summary>
    public static bool IsEscalation(ToolPolicy manifestPolicy, ToolPolicy tenantPolicy)
    {
        EnsureDefined(manifestPolicy, nameof(manifestPolicy));
        EnsureDefined(tenantPolicy, nameof(tenantPolicy));

        return tenantPolicy > manifestPolicy;
    }

    /// <summary>
    /// Действующая политика тула: переопределение тенанта, если оно не выше
    /// манифестного. Политика тенанта отсутствует — действует манифестная.
    /// </summary>
    /// <exception cref="PolicyEscalationException">Тенант пытается повысить политику.</exception>
    public static ToolPolicy ResolveEffectivePolicy(
        ToolPolicy manifestPolicy,
        ToolPolicy? tenantPolicy,
        string? toolName = null)
    {
        EnsureDefined(manifestPolicy, nameof(manifestPolicy));

        if (tenantPolicy is not { } tenant)
        {
            return manifestPolicy;
        }

        EnsureDefined(tenant, nameof(tenantPolicy));

        if (IsEscalation(manifestPolicy, tenant))
        {
            throw new PolicyEscalationException(toolName, manifestPolicy, tenant);
        }

        return tenant;
    }

    private static void EnsureDefined(ToolPolicy policy, string parameterName)
    {
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentException($"unknown tool policy value {(int)policy}", parameterName);
        }
    }
}
