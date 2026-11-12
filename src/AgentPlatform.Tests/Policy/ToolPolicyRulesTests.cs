using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Policy;

namespace AgentPlatform.Tests.Policy;

/// <summary>
/// Правила политик тулов: манифест — потолок, tool_settings тенанта может только
/// понизить политику (destructive → write → read); повышение запрещено.
/// </summary>
public sealed class ToolPolicyRulesTests
{
    [Theory]
    [InlineData(ToolPolicy.Read, ToolPolicy.Write)]
    [InlineData(ToolPolicy.Read, ToolPolicy.Destructive)]
    [InlineData(ToolPolicy.Write, ToolPolicy.Destructive)]
    public void Policy_escalation_is_detected(ToolPolicy manifestPolicy, ToolPolicy tenantPolicy)
    {
        Assert.True(ToolPolicyRules.IsEscalation(manifestPolicy, tenantPolicy));

        Assert.Throws<PolicyEscalationException>(() =>
            ToolPolicyRules.ResolveEffectivePolicy(manifestPolicy, tenantPolicy, "ms_create_order_draft"));
    }

    [Theory]
    [InlineData(ToolPolicy.Destructive, ToolPolicy.Write)]
    [InlineData(ToolPolicy.Destructive, ToolPolicy.Read)]
    [InlineData(ToolPolicy.Write, ToolPolicy.Read)]
    public void Policy_downgrade_is_allowed(ToolPolicy manifestPolicy, ToolPolicy tenantPolicy)
    {
        Assert.False(ToolPolicyRules.IsEscalation(manifestPolicy, tenantPolicy));

        Assert.Equal(
            tenantPolicy,
            ToolPolicyRules.ResolveEffectivePolicy(manifestPolicy, tenantPolicy, "ms_create_order_draft"));
    }

    [Theory]
    [InlineData(ToolPolicy.Read)]
    [InlineData(ToolPolicy.Write)]
    [InlineData(ToolPolicy.Destructive)]
    public void Equal_policy_is_allowed(ToolPolicy policy)
    {
        Assert.False(ToolPolicyRules.IsEscalation(policy, policy));

        Assert.Equal(policy, ToolPolicyRules.ResolveEffectivePolicy(policy, policy));
    }

    [Theory]
    [InlineData(ToolPolicy.Read)]
    [InlineData(ToolPolicy.Write)]
    [InlineData(ToolPolicy.Destructive)]
    public void Missing_tenant_policy_uses_manifest_policy(ToolPolicy manifestPolicy)
    {
        Assert.Equal(manifestPolicy, ToolPolicyRules.ResolveEffectivePolicy(manifestPolicy, null));
    }

    [Fact]
    public void Escalation_message_names_tool_and_policies()
    {
        var exception = Assert.Throws<PolicyEscalationException>(() =>
            ToolPolicyRules.ResolveEffectivePolicy(ToolPolicy.Read, ToolPolicy.Write, "ms_create_order_draft"));

        Assert.Equal("ms_create_order_draft", exception.ToolName);
        Assert.Equal(ToolPolicy.Read, exception.ManifestPolicy);
        Assert.Equal(ToolPolicy.Write, exception.TenantPolicy);
        Assert.Contains("read -> write", exception.Message, StringComparison.Ordinal);
        Assert.Contains("only downgrade is allowed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_policy_value_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            ToolPolicyRules.ResolveEffectivePolicy((ToolPolicy)99, null));
    }
}
