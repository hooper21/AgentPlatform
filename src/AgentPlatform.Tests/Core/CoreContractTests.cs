using System.Text.Json.Nodes;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Json;
using AgentPlatform.Core.Validation;

namespace AgentPlatform.Tests.Core;

/// <summary>
/// Контракты Core: требование гейта, валидаторы jsonb и токены БД.
/// Round-trip состояния сессии — <see cref="StateContractTests"/>,
/// манифест коннектора — <see cref="ConnectorManifestTests"/>.
/// </summary>
public sealed class CoreContractTests
{
    [Fact]
    public void Gate_requirement_factories_match_contract()
    {
        Assert.Equal(GateRequirementKind.None, GateRequirement.None.Kind);
        Assert.False(GateRequirement.None.RequiresConfirmation);

        Assert.Equal(GateRequirementKind.UserConfirmation, GateRequirement.UserConfirmation.Kind);
        Assert.True(GateRequirement.UserConfirmation.RequiresConfirmation);

        Assert.Equal(GateRequirementKind.OperatorConfirmation, GateRequirement.OperatorConfirmation.Kind);
        Assert.True(GateRequirement.OperatorConfirmation.RequiresConfirmation);

        var predicate = new GatePredicate("amount", "gt", 50_000);
        var conditional = GateRequirement.Conditional(predicate);

        Assert.Equal(GateRequirementKind.Conditional, conditional.Kind);
        Assert.Same(predicate, conditional.Predicate);
        Assert.True(conditional.RequiresConfirmation);
    }

    [Fact]
    public void Conditional_gate_requirement_requires_predicate()
    {
        Assert.Throws<ArgumentNullException>(() => GateRequirement.Conditional(null!));
    }

    [Fact]
    public void Gate_payload_validator_accepts_complete_payload()
    {
        var payload = new GatePayload(
            "echo_write",
            new JsonObject { ["message"] = "***" },
            "Отправить сообщение",
            Guid.NewGuid(),
            DateTimeOffset.UtcNow);

        GatePayloadValidator.Validate(payload);
        GatePayloadValidator.Validate(payload.ToJson());
    }

    [Theory]
    [InlineData("""{"args_masked":{},"human_description":"x"}""")]
    [InlineData("""{"tool":"echo_write","human_description":"x"}""")]
    [InlineData("""{"tool":"echo_write","args_masked":{}}""")]
    [InlineData("""{"tool":"echo_write","args_masked":{},"human_description":"x"}""")]
    [InlineData("""{"tool":"echo_write","args_masked":{},"human_description":"x","session_id":"11111111-1111-1111-1111-111111111111"}""")]
    [InlineData("""[]""")]
    [InlineData("not-json")]
    public void Gate_payload_validator_rejects_incomplete_payload(string json)
    {
        Assert.Throws<JsonValidationException>(() => GatePayloadValidator.Validate(json));
    }

    [Fact]
    public void Gate_payload_validator_requires_payload()
    {
        Assert.Throws<JsonValidationException>(() => GatePayloadValidator.Validate((string?)null));
        Assert.Throws<JsonValidationException>(() => GatePayloadValidator.Validate((GatePayload?)null));
    }

    [Fact]
    public void Agent_rules_validator_accepts_documented_schema()
    {
        var rules = new AgentRules(
            Qualification: "Уточнить бюджет",
            QualificationQuestions: ["Какой бюджет?", "Когда нужен запуск?"],
            Restrictions: ["Не обещать скидки"],
            AskBeforeWrite: true);

        AgentRulesValidator.Validate(rules);
        AgentRulesValidator.Validate("{}");
    }

    [Theory]
    [InlineData("""{"unknown_key":true}""")]
    [InlineData("""{"restrictions":"not-an-array"}""")]
    [InlineData("""{"ask_before_write":"yes"}""")]
    [InlineData("""[]""")]
    public void Agent_rules_validator_rejects_schema_violations(string json)
    {
        Assert.Throws<JsonValidationException>(() => AgentRulesValidator.Validate(json));
    }

    [Theory]
    [InlineData("apiKey")]
    [InlineData("longLivedToken")]
    [InlineData("oauth2")]
    [InlineData("external_mcp")]
    public void Connector_auth_type_tokens_match_ddl(string token)
    {
        var parsed = EnumTokens.ParseDbValue<ConnectorAuthType>(token);

        Assert.Equal(token, parsed.ToDbValue());
    }

    [Fact]
    public void Snake_case_enum_tokens_round_trip()
    {
        Assert.Equal("awaiting_gate", SessionStatus.AwaitingGate.ToDbValue());
        Assert.Equal("gate_required", ToolCallOutcome.GateRequired.ToDbValue());
        Assert.Equal("tenant_override", PolicySource.TenantOverride.ToDbValue());
        Assert.Equal("destructive", ToolPolicy.Destructive.ToDbValue());
        Assert.Equal("pending", ApprovalStatus.Pending.ToDbValue());

        Assert.Equal(SessionStatus.AwaitingGate, EnumTokens.ParseDbValue<SessionStatus>("awaiting_gate"));
        Assert.Equal(ToolCallOutcome.GateRequired, EnumTokens.ParseDbValue<ToolCallOutcome>("gate_required"));
    }

    [Fact]
    public void Unknown_enum_token_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => EnumTokens.ParseDbValue<ToolPolicy>("admin"));
    }

    [Fact]
    public void Api_json_uses_camel_case_and_db_json_uses_snake_case()
    {
        var rules = new AgentRules(Qualification: "q", AskBeforeWrite: true);

        var apiJson = PlatformJson.SerializeApi(rules);
        var dbJson = PlatformJson.SerializeDb(rules);

        Assert.Contains("\"askBeforeWrite\"", apiJson, StringComparison.Ordinal);
        Assert.Contains("\"ask_before_write\"", dbJson, StringComparison.Ordinal);
    }
}
