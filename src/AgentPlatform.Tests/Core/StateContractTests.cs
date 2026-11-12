using System.Globalization;
using System.Text.Json.Nodes;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Json;

namespace AgentPlatform.Tests.Core;

/// <summary>
/// Сериализация нейтрального состояния сессии: round-trip через StateContract,
/// токены контракта и защита от рантайм-специфичных структур.
/// </summary>
public sealed class StateContractTests
{
    private static readonly DateTimeOffset Timestamp =
        DateTimeOffset.Parse("2026-01-01T10:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void State_round_trips_through_neutral_json()
    {
        var approvalId = Guid.NewGuid();
        var expiresAt = Timestamp.AddHours(24);
        var state = new SessionState
        {
            Messages =
            [
                new StateMessage(MessageRole.User, "Нужна консультация", Timestamp),
                new StateMessage(
                    MessageRole.Assistant,
                    "Оформляю заказ",
                    Timestamp.AddSeconds(5),
                    [new StateToolCall("ms_create_order", """{"amount":50000}""", "results/1", 42)])
            ],
            PendingGate = new PendingGateState(
                approvalId,
                new GatePayload(
                    "ms_create_order",
                    new JsonObject { ["amount"] = 50000 },
                    "Создать заказ",
                    "Отменить в течение часа"),
                expiresAt),
            Counters = new SessionCounters(
                Turns: 3,
                ToolCalls: 1,
                FailedTurns: 1,
                TokensIn: 1200,
                TokensOut: 340)
        };

        var json = StateContract.Serialize(state);
        var restored = StateContract.DeserializeRequired(json);

        Assert.Equal(SessionState.CurrentVersion, restored.Version);

        Assert.Equal(2, restored.Messages.Count);
        Assert.Equal(MessageRole.User, restored.Messages[0].Role);
        Assert.Equal("Нужна консультация", restored.Messages[0].Content);
        Assert.Equal(Timestamp, restored.Messages[0].Timestamp);

        var toolCall = Assert.Single(restored.Messages[1].ToolCalls!);
        Assert.Equal("ms_create_order", toolCall.Name);
        Assert.Equal("""{"amount":50000}""", toolCall.ArgsMasked);
        Assert.Equal("results/1", toolCall.ResultRef);
        Assert.Equal(42, toolCall.LatencyMs);

        Assert.NotNull(restored.PendingGate);
        Assert.Equal(approvalId, restored.PendingGate!.ApprovalId);
        Assert.Equal("ms_create_order", restored.PendingGate.Payload.Tool);
        Assert.Equal("Создать заказ", restored.PendingGate.Payload.HumanDescription);
        Assert.Equal("Отменить в течение часа", restored.PendingGate.Payload.UndoHint);
        Assert.Equal(50000, restored.PendingGate.Payload.ArgsMasked!["amount"]!.GetValue<int>());
        Assert.Equal(expiresAt, restored.PendingGate.ExpiresAt);

        Assert.Equal(3, restored.Counters.Turns);
        Assert.Equal(1, restored.Counters.ToolCalls);
        Assert.Equal(1, restored.Counters.FailedTurns);
        Assert.Equal(1200, restored.Counters.TokensIn);
        Assert.Equal(340, restored.Counters.TokensOut);

        // Повторная сериализация даёт тот же документ: контракт стабилен.
        Assert.Equal(json, StateContract.Serialize(restored));
    }

    [Fact]
    public void Serialized_state_uses_contract_keys_only()
    {
        var json = StateContract.Serialize(new SessionState
        {
            Messages = [new StateMessage(MessageRole.User, "hi", Timestamp)]
        });

        Assert.Contains("\"version\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"messages\"", json, StringComparison.Ordinal);
        Assert.Contains("\"counters\"", json, StringComparison.Ordinal);
        Assert.Contains("\"role\":\"user\"", json, StringComparison.Ordinal);
        Assert.Contains("\"tokens_in\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"pending_gate\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_state_round_trips()
    {
        var restored = StateContract.DeserializeRequired(StateContract.Serialize(new SessionState()));

        Assert.Equal(SessionState.CurrentVersion, restored.Version);
        Assert.Empty(restored.Messages);
        Assert.Null(restored.PendingGate);
        Assert.Equal(new SessionCounters(), restored.Counters);
    }

    [Fact]
    public void Runtime_specific_fields_do_not_pass_the_contract()
    {
        // Поле нативного чекпоинта MAF: через StateContract проходить не должно.
        const string json =
            """{"version":1,"messages":[],"counters":{},"maf_checkpoint":{"superstep":1}}""";

        Assert.Throws<JsonValidationException>(() => StateContract.Deserialize(json));
        Assert.False(StateContract.TryDeserialize(json, out var state));
        Assert.Null(state);
    }

    [Fact]
    public void Unsupported_state_version_is_rejected()
    {
        Assert.Throws<JsonValidationException>(
            () => StateContract.Deserialize("""{"version":99,"messages":[],"counters":{}}"""));

        Assert.Throws<JsonValidationException>(
            () => StateContract.Serialize(new SessionState { Version = 99 }));
    }

    [Fact]
    public void Empty_message_content_is_rejected_on_write()
    {
        var state = new SessionState
        {
            Messages = [new StateMessage(MessageRole.User, "   ", Timestamp)]
        };

        Assert.Throws<JsonValidationException>(() => StateContract.Serialize(state));
    }

    [Fact]
    public void Pending_gate_without_approval_id_is_rejected()
    {
        const string json =
            """{"version":1,"messages":[],"counters":{},"pending_gate":{"approval_id":"00000000-0000-0000-0000-000000000000","payload":{"tool":"echo_write","args_masked":{},"human_description":"x"}}}""";

        Assert.Throws<JsonValidationException>(() => StateContract.Deserialize(json));
    }

    [Fact]
    public void Negative_counters_are_rejected()
    {
        var state = new SessionState { Counters = new SessionCounters(Turns: -1) };

        Assert.Throws<JsonValidationException>(() => StateContract.Serialize(state));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Absent_document_means_no_checkpoint(string? json)
    {
        Assert.Null(StateContract.Deserialize(json));
        Assert.False(StateContract.TryDeserialize(json, out _));
        Assert.Throws<JsonValidationException>(() => StateContract.DeserializeRequired(json));
    }

    [Fact]
    public void Malformed_document_is_rejected()
    {
        Assert.Throws<JsonValidationException>(() => StateContract.Deserialize("not-json"));
        Assert.False(StateContract.TryDeserialize("not-json", out var state));
        Assert.Null(state);
    }
}
