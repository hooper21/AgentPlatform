using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Json;
using AgentPlatform.Core.Policy;
using AgentPlatform.Core.Validation;

namespace AgentPlatform.Tests.Policy;

/// <summary>
/// Валидация конфигурации tool_settings: понижение разрешено, повышение —
/// ошибка конфигурации; формат gate_settings и conditional-предиката строгий;
/// отсутствие настроек — самый строгий режим.
/// </summary>
public sealed class ToolSettingsValidatorTests
{
    private const string ConditionalAmount =
        """{"mode":"conditional","predicate":{"field":"amount","op":"gt","value":50000}}""";

    [Fact]
    public void Absent_settings_use_manifest_policy_and_strict_default_mode()
    {
        var settings = ToolSettingsValidator.ParseAndValidate(
            null, null, ToolPolicy.Write, "ms_create_order_draft");

        Assert.Equal(ToolPolicy.Write, settings.ManifestPolicy);
        Assert.Equal(ToolPolicy.Write, settings.EffectivePolicy);
        Assert.Equal(PolicySource.Manifest, settings.PolicySource);
        Assert.Equal(GateMode.Always, settings.GateMode);
        Assert.Null(settings.Predicate);
        Assert.Equal(GateRequirementKind.UserConfirmation, settings.Requirement.Kind);
    }

    [Fact]
    public void Empty_gate_settings_keep_strict_default_mode()
    {
        var settings = ToolSettingsValidator.ParseAndValidate("read", "{}", ToolPolicy.Read, "ms_get_stock");

        Assert.Equal(GateMode.Always, settings.GateMode);
        Assert.Equal(GateRequirementKind.UserConfirmation, settings.Requirement.Kind);
    }

    [Fact]
    public void Policy_downgrade_is_accepted()
    {
        var settings = ToolSettingsValidator.ParseAndValidate(
            "read",
            """{"mode":"never"}""",
            ToolPolicy.Write,
            "ms_create_order_draft");

        Assert.Equal(ToolPolicy.Read, settings.EffectivePolicy);
        Assert.Equal(PolicySource.TenantOverride, settings.PolicySource);
        Assert.Equal(GateRequirementKind.None, settings.Requirement.Kind);
    }

    [Theory]
    [InlineData("write", ToolPolicy.Read)]
    [InlineData("destructive", ToolPolicy.Read)]
    [InlineData("destructive", ToolPolicy.Write)]
    public void Policy_escalation_is_rejected(string policyToken, ToolPolicy manifestPolicy)
    {
        Assert.Throws<PolicyEscalationException>(() =>
            ToolSettingsValidator.ParseAndValidate(policyToken, null, manifestPolicy, "ms_get_stock"));
    }

    [Fact]
    public void Equal_policy_is_stored_as_tenant_override()
    {
        var settings = ToolSettingsValidator.ParseAndValidate("write", null, ToolPolicy.Write, "t");

        Assert.Equal(PolicySource.TenantOverride, settings.PolicySource);
        Assert.Equal(ToolPolicy.Write, settings.EffectivePolicy);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("READ")]
    [InlineData("read-only")]
    public void Unknown_policy_token_is_rejected(string token)
    {
        Assert.Throws<JsonValidationException>(() =>
            ToolSettingsValidator.ParseAndValidate(token, null, ToolPolicy.Write, "t"));
    }

    [Fact]
    public void Conditional_predicate_is_parsed()
    {
        var settings = ToolSettingsValidator.ParseAndValidate(
            "write", ConditionalAmount, ToolPolicy.Write, "ms_create_order_draft");

        Assert.Equal(GateMode.Conditional, settings.GateMode);
        Assert.NotNull(settings.Predicate);
        Assert.Equal("amount", settings.Predicate!.Field);
        Assert.Equal("gt", settings.Predicate.Op);
        Assert.Equal(50_000, settings.Predicate.Value!.GetValue<int>());
        Assert.Equal(GateRequirementKind.Conditional, settings.Requirement.Kind);
    }

    [Theory]
    [InlineData("""{"mode":"conditional"}""")]
    [InlineData("""{"predicate":{"field":"amount","op":"gt","value":1}}""")]
    [InlineData("""{"mode":"always","predicate":{"field":"amount","op":"gt","value":1}}""")]
    [InlineData("""{"mode":"sometimes"}""")]
    [InlineData("""{"mode":"always","extra":true}""")]
    [InlineData("""{"mode":"conditional","predicate":{"field":"amount","op":"regex","value":".*"}}""")]
    [InlineData("""{"mode":"conditional","predicate":{"field":"amount","op":"gt"}}""")]
    [InlineData("""{"mode":"conditional","predicate":{"field":"amount","op":"gt","value":null}}""")]
    [InlineData("""{"mode":"conditional","predicate":{"field":"","op":"gt","value":1}}""")]
    [InlineData("""{"mode":"conditional","predicate":{"field":"amount","op":"gt","value":1,"extra":true}}""")]
    [InlineData("""{"mode":"conditional","predicate":{"amount":1}}""")]
    [InlineData("""{"mode":"conditional","predicate":[]}""")]
    [InlineData("[]")]
    [InlineData("not-json")]
    public void Invalid_gate_settings_are_rejected(string gateSettings)
    {
        Assert.Throws<JsonValidationException>(() =>
            ToolSettingsValidator.ParseAndValidate(null, gateSettings, ToolPolicy.Write, "ms_create_order_draft"));
    }

    [Fact]
    public void Eq_predicate_allows_null_value()
    {
        var settings = ToolSettingsValidator.ParseAndValidate(
            null,
            """{"mode":"conditional","predicate":{"field":"paid_at","op":"eq","value":null}}""",
            ToolPolicy.Write,
            "ms_create_order_draft");

        Assert.Equal("paid_at", settings.Predicate!.Field);
        Assert.Null(settings.Predicate.Value);
    }

    [Fact]
    public void Nested_predicate_field_is_accepted()
    {
        var settings = ToolSettingsValidator.ParseAndValidate(
            null,
            """{"mode":"conditional","predicate":{"field":"order.amount","op":"gt","value":50000}}""",
            ToolPolicy.Write,
            "ms_create_order_draft");

        Assert.Equal("order.amount", settings.Predicate!.Field);
    }

    [Fact]
    public void Validate_and_resolve_policy_returns_effective_policy()
    {
        var effective = ToolSettingsValidator.ValidateAndResolvePolicy(
            "read", """{"mode":"never"}""", ToolPolicy.Destructive, "t");

        Assert.Equal(ToolPolicy.Read, effective);
    }

    [Fact]
    public void Invalid_manifest_policy_is_rejected()
    {
        Assert.Throws<JsonValidationException>(() =>
            ToolSettingsValidator.ParseAndValidate(null, null, (ToolPolicy)42, "t"));
    }

    [Fact]
    public void Empty_tool_name_is_rejected()
    {
        Assert.Throws<JsonValidationException>(() =>
            ToolSettingsValidator.ParseAndValidate(null, null, ToolPolicy.Read, " "));
    }

    [Fact]
    public void Tool_context_from_absent_settings_has_no_overrides()
    {
        var installationId = Guid.NewGuid();

        var tool = ToolSettingsValidator.CreateToolContext(
            installationId,
            "ms_get_stock",
            ToolPolicy.Read,
            policyToken: null,
            gateSettingsJson: null);

        Assert.Equal(installationId, tool.InstallationId);
        Assert.Equal(ToolPolicy.Read, tool.ManifestPolicy);
        Assert.Null(tool.TenantPolicy);
        Assert.Null(tool.TenantGateMode);
        Assert.Null(tool.ConditionalPredicate);
    }

    [Fact]
    public void Tool_context_from_conditional_settings_exposes_mode_and_predicate()
    {
        var tool = ToolSettingsValidator.CreateToolContext(
            Guid.NewGuid(),
            "ms_create_order_draft",
            ToolPolicy.Write,
            "write",
            ConditionalAmount);

        Assert.Equal(ToolPolicy.Write, tool.TenantPolicy);
        Assert.Equal(GateMode.Conditional, tool.TenantGateMode);
        Assert.NotNull(tool.ConditionalPredicate);
        Assert.Equal("amount", tool.ConditionalPredicate!.Field);
    }

    [Fact]
    public void Tool_context_with_policy_only_keeps_tenant_default_gate_mode()
    {
        var tool = ToolSettingsValidator.CreateToolContext(
            Guid.NewGuid(),
            "ms_create_order_draft",
            ToolPolicy.Write,
            "read",
            gateSettingsJson: "{}");

        Assert.Equal(ToolPolicy.Read, tool.TenantPolicy);
        Assert.Null(tool.TenantGateMode);
    }
}

