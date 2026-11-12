using System.Text.Json.Nodes;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Policy;
using AgentPlatform.Runtime.Policy;

namespace AgentPlatform.Tests.Policy;

/// <summary>
/// Runtime-реализация Policy Engine: политика тенанта не выше манифестной,
/// conditional-предикат на аргументах, самый строгий режим по умолчанию
/// и fail-closed при ошибках конфигурации.
/// </summary>
public sealed class PolicyEngineTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid InstallationId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly PolicyEngine Engine = new();

    private static SessionContext Session(Guid? tenantId = null) => new(
        Guid.NewGuid(),
        tenantId ?? TenantId,
        Guid.NewGuid(),
        1,
        Guid.NewGuid(),
        "visitor-1");

    private static TenantPolicyContext Tenant(GateMode defaultGateMode = GateMode.Always)
        => new(TenantId, defaultGateMode);

    private static ToolContext Tool(
        ToolPolicy manifestPolicy,
        ToolPolicy? tenantPolicy = null,
        GateMode? gateMode = null,
        GatePredicate? predicate = null) => new(
            InstallationId,
            "ms_create_order_draft",
            manifestPolicy,
            tenantPolicy,
            predicate,
            gateMode);

    [Theory]
    [InlineData(ToolPolicy.Read, GateRequirementKind.UserConfirmation)]
    [InlineData(ToolPolicy.Write, GateRequirementKind.UserConfirmation)]
    [InlineData(ToolPolicy.Destructive, GateRequirementKind.OperatorConfirmation)]
    public void No_settings_use_strictest_default_mode(ToolPolicy manifestPolicy, GateRequirementKind expected)
    {
        var requirement = Engine.GetGateRequirement(Tool(manifestPolicy), Tenant(), Session());

        Assert.Equal(expected, requirement.Kind);
    }

    [Theory]
    [InlineData(ToolPolicy.Read, GateRequirementKind.None)]
    [InlineData(ToolPolicy.Write, GateRequirementKind.UserConfirmation)]
    [InlineData(ToolPolicy.Destructive, GateRequirementKind.OperatorConfirmation)]
    public void Tenant_never_default_cannot_disable_write_and_destructive_gates(
        ToolPolicy manifestPolicy,
        GateRequirementKind expected)
    {
        var requirement = Engine.GetGateRequirement(Tool(manifestPolicy), Tenant(GateMode.Never), Session());

        Assert.Equal(expected, requirement.Kind);
    }

    [Fact]
    public void Policy_downgrade_to_read_removes_gate()
    {
        var requirement = Engine.GetGateRequirement(
            Tool(ToolPolicy.Write, ToolPolicy.Read, GateMode.Never),
            Tenant(),
            Session());

        Assert.Equal(GateRequirementKind.None, requirement.Kind);
    }

    [Fact]
    public void Destructive_downgraded_to_write_requires_user_confirmation()
    {
        var requirement = Engine.GetGateRequirement(
            Tool(ToolPolicy.Destructive, ToolPolicy.Write, GateMode.Never),
            Tenant(),
            Session());

        Assert.Equal(GateRequirementKind.UserConfirmation, requirement.Kind);
    }

    [Fact]
    public void Policy_escalation_is_rejected_at_runtime()
    {
        Assert.Throws<PolicyEscalationException>(() =>
            Engine.GetGateRequirement(
                Tool(ToolPolicy.Read, ToolPolicy.Write),
                Tenant(),
                Session()));
    }

    [Fact]
    public void Never_mode_without_downgrade_keeps_write_gate()
    {
        var requirement = Engine.GetGateRequirement(
            Tool(ToolPolicy.Write, ToolPolicy.Write, GateMode.Never),
            Tenant(),
            Session());

        Assert.Equal(GateRequirementKind.UserConfirmation, requirement.Kind);
    }

    [Fact]
    public void Conditional_predicate_is_evaluated_on_arguments()
    {
        var predicate = new GatePredicate("amount", "gt", 50_000);
        var tool = Tool(ToolPolicy.Write, ToolPolicy.Write, GateMode.Conditional, predicate);

        var requirement = Engine.GetGateRequirement(tool, Tenant(), Session());
        Assert.Equal(GateRequirementKind.Conditional, requirement.Kind);
        Assert.Same(predicate, requirement.Predicate);

        var belowThreshold = Engine.EvaluateWithArguments(
            tool, Tenant(), Session(), new JsonObject { ["amount"] = 10_000 });
        Assert.Equal(GateRequirementKind.None, belowThreshold.Kind);

        var aboveThreshold = Engine.EvaluateWithArguments(
            tool, Tenant(), Session(), new JsonObject { ["amount"] = 75_000 });
        Assert.Equal(GateRequirementKind.UserConfirmation, aboveThreshold.Kind);
    }

    [Fact]
    public void Conditional_gate_on_destructive_tool_requires_operator()
    {
        var tool = Tool(
            ToolPolicy.Destructive,
            ToolPolicy.Destructive,
            GateMode.Conditional,
            new GatePredicate("amount", "gt", 50_000));

        var requirement = Engine.EvaluateWithArguments(
            tool, Tenant(), Session(), Args("""{"amount":75000}"""));

        Assert.Equal(GateRequirementKind.OperatorConfirmation, requirement.Kind);
    }

    [Fact]
    public void Conditional_gate_is_skipped_when_field_is_absent()
    {
        var tool = Tool(
            ToolPolicy.Write,
            ToolPolicy.Write,
            GateMode.Conditional,
            new GatePredicate("amount", "gt", 50_000));

        var requirement = Engine.EvaluateWithArguments(
            tool, Tenant(), Session(), Args("""{"currency":"RUB"}"""));

        Assert.Equal(GateRequirementKind.None, requirement.Kind);
    }

    [Fact]
    public void Conditional_without_predicate_fails_closed()
    {
        var requirement = Engine.GetGateRequirement(
            Tool(ToolPolicy.Write, ToolPolicy.Write, GateMode.Conditional),
            Tenant(),
            Session());

        Assert.Equal(GateRequirementKind.UserConfirmation, requirement.Kind);
    }

    [Fact]
    public void Malformed_predicate_fails_closed()
    {
        var tool = Tool(
            ToolPolicy.Write,
            ToolPolicy.Write,
            GateMode.Conditional,
            new GatePredicate("amount", "regex", ".*"));

        var requirement = Engine.GetGateRequirement(tool, Tenant(), Session());
        Assert.Equal(GateRequirementKind.UserConfirmation, requirement.Kind);

        var withArguments = Engine.EvaluateWithArguments(
            tool, Tenant(), Session(), Args("""{"amount":1}"""));

        Assert.Equal(GateRequirementKind.UserConfirmation, withArguments.Kind);
    }

    [Fact]
    public void Non_numeric_arguments_for_numeric_predicate_fail_closed()
    {
        var tool = Tool(
            ToolPolicy.Write,
            ToolPolicy.Write,
            GateMode.Conditional,
            new GatePredicate("amount", "gt", 50_000));

        var requirement = Engine.EvaluateWithArguments(
            tool, Tenant(), Session(), Args("""{"amount":"many"}"""));

        Assert.Equal(GateRequirementKind.UserConfirmation, requirement.Kind);
    }

    [Fact]
    public void Session_and_tenant_mismatch_is_rejected()
    {
        var otherTenant = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() =>
            Engine.GetGateRequirement(Tool(ToolPolicy.Write), Tenant(), Session(otherTenant)));
    }

    [Fact]
    public void Effective_policy_exposes_downgrade_for_callers()
    {
        Assert.Equal(
            ToolPolicy.Read,
            Engine.ResolveEffectivePolicy(Tool(ToolPolicy.Write, ToolPolicy.Read)));

        Assert.Throws<PolicyEscalationException>(() =>
            Engine.ResolveEffectivePolicy(Tool(ToolPolicy.Read, ToolPolicy.Destructive)));
    }

    private static JsonNode Args(string json) => JsonNode.Parse(json)!;
}
