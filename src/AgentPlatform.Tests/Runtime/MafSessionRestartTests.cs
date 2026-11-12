using AgentPlatform.ConnectorHost.Tools;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;
using Microsoft.Extensions.AI;

namespace AgentPlatform.Tests.Runtime;

/// <summary>
/// Сессия переживает рестарт процесса: состояние восстанавливается из чекпоинта
/// (нейтральный JSON в checkpoints), и диалог продолжается с того же места —
/// история, счётчики и версия агента не теряются.
/// </summary>
public sealed class MafSessionRestartTests
{
    [Fact]
    public async Task Dialog_continues_after_process_restart_between_turns()
    {
        using var harness = new RuntimeHarness(EchoAgentConfig.Create(), new ScriptedChatClient());
        harness.ChatClient
            .EnqueueToolCall(
                EchoToolsManifest.ReadTool,
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["message"] = "первый" })
            .EnqueueReply("Первый ответ");

        var session = harness.CreateSession();

        // Процесс #1: свой DbContext (соединение с БД), кеш и AgentFactory.
        var firstContext = harness.CreateContext();
        var first = await harness
            .CreateRuntime(firstContext)
            .RunAsync(session, harness.CreateMessage(session, "Первое сообщение"), CancellationToken.None);

        Assert.Equal(RunOutcome.Completed, first.Outcome);
        Assert.Equal("Первый ответ", first.Message);

        var superstepAfterFirstProcess = first.Superstep;
        await firstContext.DisposeAsync();

        // Процесс #2: новый DbContext над той же БД, новый Runtime (пустой кеш, новая фабрика).
        harness.ChatClient.EnqueueReply("Второй ответ");

        var second = await harness
            .CreateRuntime()
            .RunAsync(session, harness.CreateMessage(session, "Второе сообщение"), CancellationToken.None);

        Assert.Equal(RunOutcome.Completed, second.Outcome);
        Assert.Equal("Второй ответ", second.Message);
        Assert.True(
            second.Superstep > superstepAfterFirstProcess,
            $"supersteps must continue after restart ({superstepAfterFirstProcess} -> {second.Superstep})");

        // Модель во втором процессе получила историю из чекпоинта, а не пустой диалог.
        var history = harness.ChatClient.Calls[^1];
        Assert.Equal(ChatRole.System, history[0].Role);

        var texts = history.Select(message => message.Text).ToList();
        Assert.Contains("Первое сообщение", texts);
        Assert.Contains("Первый ответ", texts);
        Assert.Equal("Второе сообщение", history[^1].Text);

        var state = harness.LatestState(session.SessionId);
        Assert.Equal(3, state.Counters.Turns);
        Assert.Equal(1, state.Counters.ToolCalls);
        Assert.Null(state.PendingGate);
        Assert.Equal(MessageRole.Assistant, state.Messages[^1].Role);
        Assert.Equal("Второй ответ", state.Messages[^1].Content);

        // История диалога не потерялась и не задвоилась.
        Assert.Equal(2, state.Messages.Count(message => message.Role == MessageRole.User));
        Assert.Equal(2, state.Messages.Count(message => message.Role == MessageRole.Assistant && message.Content.Contains("ответ")));
        Assert.Single(harness.ToolInvoker.Requests);
    }

    [Fact]
    public async Task Checkpoint_state_is_neutral_after_restart()
    {
        using var harness = new RuntimeHarness(EchoAgentConfig.Create(), new ScriptedChatClient());
        harness.ChatClient
            .EnqueueToolCall(EchoToolsManifest.ReadTool, new Dictionary<string, object?>(StringComparer.Ordinal) { ["message"] = "x" })
            .EnqueueReply("Ответ");

        var session = harness.CreateSession();
        await harness
            .CreateRuntime()
            .RunAsync(session, harness.CreateMessage(session, "Привет"), CancellationToken.None);

        var checkpoint = harness.LatestCheckpoint(session.SessionId);

        // В БД лежит только нейтральный контракт: нативные структуры MAF не проходят.
        Assert.Equal(AgentPlatform.Runtime.MafOrchestrationRuntime.GraphSignatureHash, checkpoint.GraphSignatureHash);
        Assert.Contains("\"version\":1", checkpoint.State, StringComparison.Ordinal);
        Assert.Contains("\"messages\"", checkpoint.State, StringComparison.Ordinal);
        Assert.DoesNotContain("checkpoint", checkpoint.State, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("superstep", checkpoint.State, StringComparison.OrdinalIgnoreCase);
    }
}
