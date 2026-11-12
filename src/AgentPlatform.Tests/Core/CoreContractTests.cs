using System.Text.Json.Nodes;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Json;
using AgentPlatform.Core.Validation;

namespace AgentPlatform.Tests.Core;

/// <summary>Контракты Core: сериализация состояния, валидаторы jsonb и токены БД.</summary>
public sealed class CoreContractTests
{
    [Fact]
    public void Session_state_contract_round_trips_as_neutral_json()
    {
        var state = new SessionStateContract
        {
            Messages =
            [
                new StateMessage(
                    MessageRole.User.ToDbValue(),
                    "Нужна консультация",
                    DateTimeOffset.Parse("2026-01-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture))
            ],
            PendingGate = new PendingGateState(
                Guid.NewGuid(),
                new GatePayload("echo_write", new JsonObject { ["message"] = "***" }, "Отправить сообщение", "Отменить нельзя")),
            Counters = new SessionCounters(Turns: 3, ToolCalls: 1)
        };

        var json = state.ToJson();
        state.Validate();

        Assert.Contains("\"pending_gate\"", json, StringComparison.Ordinal);
        Assert.Contains("\"human_description\"", json, StringComparison.Ordinal);

        var restored = SessionStateContract.FromJson(json);

        Assert.NotNull(restored);
        Assert.Equal(SessionStateContract.CurrentVersion, restored.Version);
        Assert.Single(restored.Messages);
        Assert.Equal("echo_write", restored.PendingGate?.Payload.Tool);
        Assert.Equal(3, restored.Counters.Turns);
    }

    [Fact]
    public void Session_state_with_unsupported_version_is_rejected()
    {
        var state = new SessionStateContract { Version = 99 };

        Assert.Throws<JsonValidationException>(state.Validate);
    }

    [Fact]
    public void Gate_payload_validator_accepts_complete_payload()
    {
        var payload = new GatePayload("echo_write", new JsonObject { ["message"] = "***" }, "Отправить сообщение");

        GatePayloadValidator.Validate(payload);
        GatePayloadValidator.Validate(payload.ToJson());
    }

    [Theory]
    [InlineData("""{"args_masked":{},"human_description":"x"}""")]
    [InlineData("""{"tool":"echo_write","human_description":"x"}""")]
    [InlineData("""{"tool":"echo_write","args_masked":{}}""")]
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
