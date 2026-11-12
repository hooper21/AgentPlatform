using System.Text.Json.Nodes;
using AgentPlatform.ConnectorHost.Tools;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;

namespace AgentPlatform.Tests.Runtime;

/// <summary>
/// Resume с чекпоинта: решение по гейту продолжает граф, состояние восстанавливается
/// из нейтрального JSON, отклонённое/истёкшее действие тул не вызывает.
/// </summary>
public sealed class MafOrchestrationResumeTests
{
    [Fact]
    public async Task Approved_gate_resumes_graph_and_invokes_tool()
    {
        using var harness = new RuntimeHarness(EchoAgentConfig.Create(), new ScriptedChatClient());
        harness.ChatClient
            .EnqueueToolCall(
                EchoToolsManifest.WriteTool,
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["message"] = "запись" })
            .EnqueueReply("Записал: echo(write): запись");

        var session = harness.CreateSession();
        var paused = await harness
            .CreateRuntime()
            .RunAsync(session, harness.CreateMessage(session, "Запиши"), CancellationToken.None);

        Assert.Equal(RunOutcome.PausedForGate, paused.Outcome);
        Assert.Empty(harness.ToolInvoker.Requests);

        // Пауза идемпотентна: чекпоинт уже записан gate-узлом.
        await harness.CreateRuntime().PauseAsync(session, paused.PendingGate!);

        // «Новый процесс»: новый экземпляр Runtime восстанавливает состояние из чекпоинта.
        await harness
            .CreateRuntime()
            .ResumeAsync(
                session,
                new Decision(ApprovalStatus.Approved, ApprovalDecisionKind.Operator, Comment: "ок"));

        var request = Assert.Single(harness.ToolInvoker.Requests);
        Assert.Equal(EchoToolsManifest.WriteTool, request.ToolName);
        Assert.Contains("запись", request.ArgumentsJson, StringComparison.Ordinal);

        var state = harness.LatestState(session.SessionId);
        Assert.Null(state.PendingGate);
        Assert.Contains(
            state.Messages,
            message => message.Role == MessageRole.Tool && message.Content.Contains("echo(write): запись", StringComparison.Ordinal));

        Assert.Equal(MessageRole.Assistant, state.Messages[^1].Role);
        Assert.Equal("Записал: echo(write): запись", state.Messages[^1].Content);
    }

    [Fact]
    public async Task Rejected_gate_does_not_invoke_tool_and_dialog_continues()
    {
        using var harness = new RuntimeHarness(EchoAgentConfig.Create(), new ScriptedChatClient());
        harness.ChatClient
            .EnqueueToolCall(
                EchoToolsManifest.WriteTool,
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["message"] = "запись" })
            .EnqueueReply("Хорошо, запись отменена.");

        var session = harness.CreateSession();
        var paused = await harness
            .CreateRuntime()
            .RunAsync(session, harness.CreateMessage(session, "Запиши"), CancellationToken.None);

        await harness
            .CreateRuntime()
            .ResumeAsync(session, new Decision(ApprovalStatus.Rejected, ApprovalDecisionKind.User));

        Assert.Empty(harness.ToolInvoker.Requests);

        var state = harness.LatestState(session.SessionId);
        Assert.Null(state.PendingGate);
        Assert.Contains(
            state.Messages,
            message => message.Role == MessageRole.System && message.Content.Contains("отклонено", StringComparison.Ordinal));
        Assert.Equal("Хорошо, запись отменена.", state.Messages[^1].Content);
    }

    [Theory]
    [InlineData(ApprovalStatus.Expired)]
    [InlineData(ApprovalStatus.Superseded)]
    public async Task Terminal_non_approved_decisions_skip_tool_invocation(ApprovalStatus status)
    {
        using var harness = new RuntimeHarness(EchoAgentConfig.Create(), new ScriptedChatClient());
        harness.ChatClient
            .EnqueueToolCall(EchoToolsManifest.WriteTool)
            .EnqueueReply("Действие не выполнено.");

        var session = harness.CreateSession();
        await harness
            .CreateRuntime()
            .RunAsync(session, harness.CreateMessage(session, "Запиши"), CancellationToken.None);

        await harness
            .CreateRuntime()
            .ResumeAsync(session, new Decision(status, ApprovalDecisionKind.System));

        Assert.Empty(harness.ToolInvoker.Requests);
        Assert.Null(harness.LatestState(session.SessionId).PendingGate);
    }

    [Fact]
    public async Task Visitor_cannot_approve_destructive_tool()
    {
        using var harness = new RuntimeHarness(
            EchoAgentConfig.Create(writePolicy: ToolPolicy.Destructive),
            new ScriptedChatClient());

        harness.ChatClient
            .EnqueueToolCall(EchoToolsManifest.WriteTool)
            .EnqueueReply("Нужен оператор.");

        var session = harness.CreateSession();
        await harness
            .CreateRuntime()
            .RunAsync(session, harness.CreateMessage(session, "Снеси всё"), CancellationToken.None);

        await harness
            .CreateRuntime()
            .ResumeAsync(session, new Decision(ApprovalStatus.Approved, ApprovalDecisionKind.User));

        Assert.Empty(harness.ToolInvoker.Requests);

        var state = harness.LatestState(session.SessionId);
        Assert.Null(state.PendingGate);
        Assert.Contains(
            state.Messages,
            message => message.Role == MessageRole.System
                && message.Content.Contains("подтверждение оператора", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Resume_requires_a_checkpoint_with_pending_gate()
    {
        using var harness = new RuntimeHarness(EchoAgentConfig.Create(), new ScriptedChatClient());
        var session = harness.CreateSession();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.CreateRuntime().ResumeAsync(
                session,
                new Decision(ApprovalStatus.Approved, ApprovalDecisionKind.Operator)));
    }

    [Fact]
    public async Task Pending_decision_cannot_resume_graph()
    {
        using var harness = new RuntimeHarness(EchoAgentConfig.Create(), new ScriptedChatClient());
        var session = harness.CreateSession();

        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.CreateRuntime().ResumeAsync(
                session,
                new Decision(ApprovalStatus.Pending, ApprovalDecisionKind.Operator)));
    }

    [Fact]
    public async Task Resume_uses_pinned_version_and_rejects_lost_tool()
    {
        var configStore = new FakeAgentConfigStore(EchoAgentConfig.Create());
        using var harness = new RuntimeHarness(
            EchoAgentConfig.Create(),
            new ScriptedChatClient(),
            configStore: configStore);

        harness.ChatClient.EnqueueToolCall(EchoToolsManifest.WriteTool);

        var session = harness.CreateSession();
        await harness
            .CreateRuntime()
            .RunAsync(session, harness.CreateMessage(session, "Запиши"), CancellationToken.None);

        // Конфигурация закреплённой версии изменилась: тул больше не доступен.
        configStore.Set(EchoAgentConfig.WithoutTool(EchoToolsManifest.WriteTool));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.CreateRuntime().ResumeAsync(
                session,
                new Decision(ApprovalStatus.Approved, ApprovalDecisionKind.Operator)));

        Assert.Contains("pinned", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.ToolInvoker.Requests);
    }

    [Fact]
    public async Task Pause_requires_matching_pending_gate()
    {
        using var harness = new RuntimeHarness(EchoAgentConfig.Create(), new ScriptedChatClient());
        harness.ChatClient.EnqueueToolCall(EchoToolsManifest.WriteTool);

        var session = harness.CreateSession();
        var runtime = harness.CreateRuntime();

        var newGate = new GatePayload(
            EchoToolsManifest.WriteTool,
            new JsonObject { ["message"] = "запись" },
            "другой гейт",
            session.SessionId,
            DateTimeOffset.UtcNow);

        // Чекпоинта ещё нет.
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.PauseAsync(session, newGate));

        var paused = await runtime.RunAsync(session, harness.CreateMessage(session, "Запиши"), CancellationToken.None);
        Assert.NotNull(paused.PendingGate);

        // Другой тул не может заменить уже ожидающий гейт.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.PauseAsync(
                session,
                new GatePayload(
                    EchoToolsManifest.ReadTool,
                    new JsonObject { ["message"] = "чтение" },
                    "чтение",
                    session.SessionId,
                    DateTimeOffset.UtcNow)));

        // Тот же гейт — идемпотентно.
        await runtime.PauseAsync(session, paused.PendingGate!);
    }


    [Fact]
    public async Task Checkpoint_with_other_graph_signature_is_rejected()
    {
        using var harness = new RuntimeHarness(EchoAgentConfig.Create(), new ScriptedChatClient());
        var session = harness.CreateSession();
        var context = harness.CreateContext();

        await new AgentPlatform.Infrastructure.Repositories.EfCheckpointStore(context).SaveAsync(
            new SessionCheckpoint(
                EchoAgentConfig.TenantId,
                session.SessionId,
                1,
                StateContract.Serialize(new SessionState
                {
                    Messages = [new StateMessage(MessageRole.User, "старый ход", DateTimeOffset.UtcNow)]
                }),
                "agentplatform.maf.graph.v0",
                DateTimeOffset.UtcNow));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.CreateRuntime(context).RunAsync(
                session,
                harness.CreateMessage(session, "Привет"),
                CancellationToken.None));

        Assert.Contains("signature", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

}
