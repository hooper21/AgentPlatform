using AgentPlatform.ConnectorHost.Tools;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;
using AgentPlatform.Runtime;
using Microsoft.Extensions.AI;

namespace AgentPlatform.Tests.Runtime;

/// <summary>
/// Граф оркестратора: LLM-ход → условный переход → tool (read) / gate (write) /
/// escalation → end. Проверяются read-путь, пауза на гейте, отказ неизвестного тула
/// и эскалация по лимитам.
/// </summary>
public sealed class MafOrchestrationGraphTests
{
    [Fact]
    public async Task Read_tool_call_executes_tool_and_completes_turn()
    {
        using var harness = new RuntimeHarness(EchoAgentConfig.Create(), new ScriptedChatClient());
        harness.ChatClient
            .EnqueueToolCall(
                EchoToolsManifest.ReadTool,
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["message"] = "привет" })
            .EnqueueReply("Готово: echo: привет");

        var session = harness.CreateSession();
        var result = await harness
            .CreateRuntime()
            .RunAsync(session, harness.CreateMessage(session, "Прочитай"), CancellationToken.None);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal("Готово: echo: привет", result.Message);
        Assert.Equal(MafOrchestrationRuntime.GraphSignatureHash, result.GraphSignatureHash);
        Assert.Null(result.PendingGate);
        Assert.Null(result.ApprovalId);
        Assert.True(result.Superstep >= 3, $"expected at least 3 supersteps, got {result.Superstep}");

        var request = Assert.Single(harness.ToolInvoker.Requests);
        Assert.Equal(EchoToolsManifest.ReadTool, request.ToolName);
        Assert.Contains("привет", request.ArgumentsJson, StringComparison.Ordinal);

        var state = harness.LatestState(session.SessionId);
        Assert.Contains(state.Messages, message => message.Role == MessageRole.User && message.Content == "Прочитай");
        Assert.Contains(
            state.Messages,
            message => message.Role == MessageRole.Tool && message.Content.Contains("echo: привет", StringComparison.Ordinal));

        var last = state.Messages[^1];
        Assert.Equal(MessageRole.Assistant, last.Role);
        Assert.Equal("Готово: echo: привет", last.Content);

        Assert.Null(state.PendingGate);
        Assert.Equal(2, state.Counters.Turns);
        Assert.Equal(1, state.Counters.ToolCalls);

        // Модели передаются только декларации тулов: исполняет их граф, а не LLM-клиент.
        var tools = harness.ChatClient.ToolOptions[0]!.Tools!;
        Assert.Contains(tools, tool => tool.Name == EchoToolsManifest.ReadTool);
        Assert.Contains(tools, tool => tool.Name == EchoToolsManifest.WriteTool);
        Assert.All(tools, tool => Assert.IsAssignableFrom<AIFunctionDeclaration>(tool));
        Assert.DoesNotContain(tools, tool => tool is AIFunction);
    }

    [Fact]
    public async Task Write_tool_call_pauses_at_gate_without_invoking_tool()
    {
        using var harness = new RuntimeHarness(EchoAgentConfig.Create(), new ScriptedChatClient());
        harness.ChatClient.EnqueueToolCall(
            EchoToolsManifest.WriteTool,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["message"] = "удалить заказ" });

        var session = harness.CreateSession();
        var result = await harness
            .CreateRuntime()
            .RunAsync(session, harness.CreateMessage(session, "Удали заказ"), CancellationToken.None);

        Assert.Equal(RunOutcome.PausedForGate, result.Outcome);
        Assert.Null(result.Message);
        Assert.NotNull(result.PendingGate);
        Assert.Equal(EchoToolsManifest.WriteTool, result.PendingGate!.Tool);
        Assert.Equal("удалить заказ", result.PendingGate.ArgsMasked!["message"]!.GetValue<string>());
        Assert.NotNull(result.ApprovalId);
        Assert.NotEqual(Guid.Empty, result.ApprovalId!.Value);

        Assert.Empty(harness.ToolInvoker.Requests);

        var state = harness.LatestState(session.SessionId);
        Assert.NotNull(state.PendingGate);
        Assert.Equal(result.ApprovalId, state.PendingGate!.ApprovalId);
        Assert.Equal(EchoToolsManifest.WriteTool, state.PendingGate.Payload.Tool);
        Assert.NotNull(state.PendingGate.ExpiresAt);
    }

    [Fact]
    public async Task Destructive_tool_requires_operator_confirmation()
    {
        var config = EchoAgentConfig.Create(writePolicy: ToolPolicy.Destructive);
        using var harness = new RuntimeHarness(config, new ScriptedChatClient());
        harness.ChatClient.EnqueueToolCall(EchoToolsManifest.WriteTool);

        var session = harness.CreateSession();
        var result = await harness
            .CreateRuntime()
            .RunAsync(session, harness.CreateMessage(session, "Снеси всё"), CancellationToken.None);

        Assert.Equal(RunOutcome.PausedForGate, result.Outcome);
        Assert.Contains("оператора", result.PendingGate!.HumanDescription, StringComparison.Ordinal);
        Assert.Empty(harness.ToolInvoker.Requests);
    }

    [Fact]
    public async Task Unknown_tool_is_denied_and_dialog_continues()
    {
        using var harness = new RuntimeHarness(EchoAgentConfig.Create(), new ScriptedChatClient());
        harness.ChatClient
            .EnqueueToolCall("shell_exec", new Dictionary<string, object?>(StringComparer.Ordinal) { ["cmd"] = "rm -rf /" })
            .EnqueueReply("Такого инструмента нет.");

        var session = harness.CreateSession();
        var result = await harness
            .CreateRuntime()
            .RunAsync(session, harness.CreateMessage(session, "Выполни команду"), CancellationToken.None);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal("Такого инструмента нет.", result.Message);
        Assert.Empty(harness.ToolInvoker.Requests);

        var state = harness.LatestState(session.SessionId);
        Assert.Contains(
            state.Messages,
            message => message.Role == MessageRole.Tool && message.Content.Contains("shell_exec", StringComparison.Ordinal));
        Assert.Equal(1, state.Counters.ToolCalls);
    }

    [Fact]
    public async Task Tool_call_limit_escalates_dialog()
    {
        using var harness = new RuntimeHarness(EchoAgentConfig.Create(maxToolCalls: 1), new ScriptedChatClient());
        harness.ChatClient
            .EnqueueToolCall(EchoToolsManifest.ReadTool, new Dictionary<string, object?>(StringComparer.Ordinal) { ["message"] = "раз" })
            .EnqueueToolCall(EchoToolsManifest.ReadTool, new Dictionary<string, object?>(StringComparer.Ordinal) { ["message"] = "два" });

        var session = harness.CreateSession();
        var result = await harness
            .CreateRuntime()
            .RunAsync(session, harness.CreateMessage(session, "Читай дважды"), CancellationToken.None);

        Assert.Equal(RunOutcome.Escalated, result.Outcome);
        Assert.NotNull(result.Message);
        Assert.Contains("лимит", result.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Single(harness.ToolInvoker.Requests);
    }

    [Fact]
    public async Task Llm_failure_degrades_turn_and_escalates_on_threshold()
    {
        using var harness = new RuntimeHarness(
            EchoAgentConfig.Create(failedTurnsThreshold: 2),
            new ScriptedChatClient());

        harness.ChatClient.EnqueueError("provider is down");

        var session = harness.CreateSession();
        var degraded = await harness
            .CreateRuntime()
            .RunAsync(session, harness.CreateMessage(session, "Привет"), CancellationToken.None);

        Assert.Equal(RunOutcome.Degraded, degraded.Outcome);
        Assert.NotNull(degraded.Message);

        var escalated = await harness
            .CreateRuntime()
            .RunAsync(session, harness.CreateMessage(session, "Привет ещё раз"), CancellationToken.None);

        Assert.Equal(RunOutcome.Escalated, escalated.Outcome);
    }

    [Fact]
    public async Task Run_is_rejected_while_session_is_awaiting_gate()
    {
        using var harness = new RuntimeHarness(EchoAgentConfig.Create(), new ScriptedChatClient());
        harness.ChatClient.EnqueueToolCall(EchoToolsManifest.WriteTool);

        var session = harness.CreateSession();
        var runtime = harness.CreateRuntime();

        await runtime.RunAsync(session, harness.CreateMessage(session, "Запиши"), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.RunAsync(session, harness.CreateMessage(session, "Ещё"), CancellationToken.None));

        Assert.Contains("ResumeAsync", exception.Message, StringComparison.Ordinal);
    }

}
