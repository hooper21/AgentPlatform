using AgentPlatform.ConnectorHost.Tools;
using AgentPlatform.Core.Enums;
using AgentPlatform.Gateway.Contracts;
using AgentPlatform.Gateway.Errors;
using AgentPlatform.Gateway.RateLimiting;
using AgentPlatform.Gateway.Resilience;
using AgentPlatform.Tests.Runtime;

namespace AgentPlatform.Tests.Gateway;

/// <summary>
/// Интеграционные тесты Gateway: сообщение канала → агент (EchoTools) → ответ,
/// SSE-поток, идентификация визитера, rate-limit и деградация при недоступной LLM.
/// Собираются те же сервисы, что и в Program.cs (InMemory-БД вместо PostgreSQL).
/// </summary>
public sealed class GatewayMessageIntegrationTests
{
    [Fact]
    public async Task Message_goes_through_agent_with_echo_tools_and_is_streamed()
    {
        var chat = new ScriptedChatClient()
            .EnqueueToolCall(
                EchoToolsManifest.ReadTool,
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["message"] = "привет" })
            .EnqueueReply("Готово: echo: привет");

        using var harness = new GatewayHarness(chat);

        var turn = await harness.SendAsync(new IncomingChannelMessage("Прочитай"));

        Assert.Equal("completed", turn.Response.Outcome);
        Assert.Equal("active", turn.Response.Status);
        Assert.Equal("Готово: echo: привет", turn.Response.Reply);
        Assert.Equal(GatewayHarness.TenantId, turn.Response.TenantId);
        Assert.StartsWith("v-", turn.Response.UserRef, StringComparison.Ordinal);

        // Первый визит: Gateway выдал идентификатор и cookie.
        Assert.Equal(turn.Response.UserRef, turn.VisitorCookie);

        var toolRequest = Assert.Single(harness.ToolInvoker.Requests);
        Assert.Equal(EchoToolsManifest.ReadTool, toolRequest.ToolName);
        Assert.Equal(GatewayHarness.TenantId, toolRequest.TenantId);

        var session = harness.FindSession(turn.Response.SessionId);
        Assert.NotNull(session);
        Assert.Equal("active", session!.Status);
        Assert.Equal(turn.Response.UserRef, session.UserRef);
        Assert.Equal(GatewayHarness.ChannelId, session.ChannelId);
        Assert.Equal(GatewayHarness.AgentId, session.AgentId);
        Assert.Equal(1, session.AgentVersion);

        var messages = harness.FindMessages(turn.Response.SessionId);
        Assert.Equal(2, messages.Count);
        Assert.Equal(MessageRole.User.ToDbValue(), messages[0].Role);
        Assert.Equal("Прочитай", messages[0].Content);
        Assert.Equal(MessageRole.Assistant.ToDbValue(), messages[1].Role);
        Assert.Equal("Готово: echo: привет", messages[1].Content);

        var stream = await harness.ReadStreamAsync(turn.Response.SessionId);
        Assert.Equal(
            [ReplyStreamEvent.StatusType, ReplyStreamEvent.DeltaType, ReplyStreamEvent.DoneType],
            stream.Select(streamEvent => streamEvent.Type));
        Assert.Equal("Готово: echo: привет", stream[1].Delta);
        Assert.Equal("completed", stream[2].Outcome);
        Assert.Equal("Готово: echo: привет", stream[2].Message);
    }

    [Fact]
    public async Task Write_tool_pauses_session_and_streams_gate_card()
    {
        var chat = new ScriptedChatClient().EnqueueToolCall(
            EchoToolsManifest.WriteTool,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["message"] = "удалить заказ" });

        using var harness = new GatewayHarness(chat);

        var turn = await harness.SendAsync(new IncomingChannelMessage("Удали заказ"));

        Assert.Equal("paused_for_gate", turn.Response.Outcome);
        Assert.Equal("awaiting_gate", turn.Response.Status);
        Assert.Null(turn.Response.Reply);
        Assert.NotNull(turn.Response.ApprovalId);
        Assert.NotNull(turn.Response.GateCard);

        // write-тул не исполняется до подтверждения.
        Assert.Empty(harness.ToolInvoker.Requests);

        var session = harness.FindSession(turn.Response.SessionId);
        Assert.Equal(SessionStatus.AwaitingGate.ToDbValue(), session!.Status);

        var stream = await harness.ReadStreamAsync(turn.Response.SessionId);
        Assert.Contains(stream, streamEvent => streamEvent.Type == ReplyStreamEvent.GateType);
        Assert.Equal(ReplyStreamEvent.DoneType, stream[^1].Type);
        Assert.Equal("awaiting_gate", stream[^1].Status);
    }

    [Fact]
    public async Task Visitor_is_identified_by_cookie_between_requests()
    {
        var chat = new ScriptedChatClient()
            .EnqueueReply("первый ответ")
            .EnqueueReply("второй ответ");

        using var harness = new GatewayHarness(chat);

        var first = await harness.SendAsync(new IncomingChannelMessage("Привет"));
        Assert.NotNull(first.VisitorCookie);
        Assert.Null(first.Response.ApprovalId);

        var second = await harness.SendAsync(
            new ChannelRequest(harness.CreateToken(), VisitorCookieRef: first.VisitorCookie),
            new IncomingChannelMessage("Ещё вопрос"));

        Assert.Equal(first.Response.UserRef, second.Response.UserRef);
        Assert.Null(second.VisitorCookie);
        Assert.Equal(first.Response.SessionId, second.Response.SessionId);
        Assert.Equal("второй ответ", second.Response.Reply);
    }

    [Fact]
    public async Task User_ref_from_jwt_token_is_trusted_and_no_cookie_is_issued()
    {
        var chat = new ScriptedChatClient().EnqueueReply("ответ");

        using var harness = new GatewayHarness(chat);

        var turn = await harness.SendAsync(
            new ChannelRequest(harness.CreateToken(userRef: "visitor-from-token")),
            new IncomingChannelMessage("Привет"));

        Assert.Equal("visitor-from-token", turn.Response.UserRef);
        Assert.Null(turn.VisitorCookie);
        Assert.Equal("visitor-from-token", harness.FindSession(turn.Response.SessionId)!.UserRef);
    }

    [Fact]
    public async Task Unavailable_llm_is_retried_then_fallback_marks_session_degraded()
    {
        var chat = new FailingChatClient();
        var resilience = new LlmResilienceOptions { Retries = 2, RetryDelay = TimeSpan.Zero };

        using var harness = new GatewayHarness(chat, resilience);

        var turn = await harness.SendAsync(new IncomingChannelMessage("Привет"));

        // 1 попытка + 2 повтора на уровне клиента модели.
        Assert.Equal(3, chat.Attempts);

        Assert.Equal("degraded", turn.Response.Outcome);
        Assert.Equal("degraded", turn.Response.Status);
        Assert.Equal(LlmResilienceOptions.DefaultFallbackReply, turn.Response.Reply);
        Assert.Contains("менеджер ответит лично", turn.Response.Reply, StringComparison.Ordinal);

        var session = harness.FindSession(turn.Response.SessionId);
        Assert.Equal(SessionStatus.Degraded.ToDbValue(), session!.Status);

        var messages = harness.FindMessages(turn.Response.SessionId);
        Assert.Equal(LlmResilienceOptions.DefaultFallbackReply, messages[^1].Content);

        var stream = await harness.ReadStreamAsync(turn.Response.SessionId);
        Assert.Equal("degraded", stream[^1].Outcome);
    }

    [Fact]
    public async Task Rate_limit_per_tenant_returns_retry_after()
    {
        var chat = new ScriptedChatClient().EnqueueReply("ответ");

        using var harness = new GatewayHarness(chat, rateLimit: new RateLimitOptions
        {
            Storage = "memory",
            Capacity = 1,
            TokensPerSecond = 0.001,
            Cost = 1
        });

        var first = await harness.SendAsync(new IncomingChannelMessage("Первое"));
        Assert.Equal("ответ", first.Response.Reply);

        var exception = await Assert.ThrowsAsync<GatewayException>(
            async () => await harness.SendAsync(new IncomingChannelMessage("Второе")));

        Assert.Equal(429, exception.StatusCode);
        Assert.Equal("rate_limited", exception.Code);
        Assert.True(exception.RetryAfter > TimeSpan.Zero);
    }
}
