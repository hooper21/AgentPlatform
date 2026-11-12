using AgentPlatform.ConnectorHost.Tools;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;

namespace AgentPlatform.Tests.Runtime;

/// <summary>
/// Полный жизненный цикл HITL (GateExecutor): заявка approvals создаётся до паузы,
/// ожидающая сессия не удерживает поток, resume исполняет/не исполняет тул по решению,
/// таймаут берётся из настройки тенанта (по умолчанию 24 ч), карточка возвращается
/// в RunResult как JSON. Покрываются edge-cases: подтверждение после рестарта,
/// две сессии тенанта с разными гейтами, отмена сессии в ожидании, version pinning.
/// </summary>
public sealed class MafHitlLifecycleTests
{
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task Gate_pause_creates_pending_approval_and_returns_card_json()
    {
        using var harness = new RuntimeHarness(EchoAgentConfig.Create(), new ScriptedChatClient());
        harness.ChatClient.EnqueueToolCall(
            EchoToolsManifest.WriteTool,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["message"] = "отправить счёт",
                ["email"] = "client@example.com"
            });

        var session = harness.CreateSession();
        var before = DateTimeOffset.UtcNow;

        // Ожидающая сессия не удерживает поток: RunAsync возвращается на паузе.
        var result = await harness
            .CreateRuntime()
            .RunAsync(session, harness.CreateMessage(session, "Отправь счёт"), CancellationToken.None);

        Assert.Equal(RunOutcome.PausedForGate, result.Outcome);
        Assert.Empty(harness.ToolInvoker.Requests);
        Assert.NotNull(result.ApprovalId);

        // Заявка в approvals: pending, session_id и маскированные аргументы.
        var approval = harness.FindApproval(result.ApprovalId!.Value);
        Assert.NotNull(approval);
        Assert.Equal(ApprovalStatus.Pending, approval.Status);
        Assert.Equal(session.SessionId, approval.SessionId);
        Assert.Equal(session.SessionId, approval.Payload.SessionId);
        Assert.Equal("***", approval.Payload.ArgsMasked!["email"]!.GetValue<string>());
        Assert.DoesNotContain("client@example.com", approval.Payload.HumanDescription, StringComparison.Ordinal);

        // Таймаут по умолчанию — 24 часа от момента паузы.
        Assert.InRange(
            approval.ExpiresAt,
            before.AddHours(24) - ClockTolerance,
            before.AddHours(24) + ClockTolerance);

        // Карточка для каналов возвращается как JSON (рендеринг — фаза 2).
        Assert.NotNull(result.GateCard);
        Assert.DoesNotContain("client@example.com", result.GateCard!, StringComparison.Ordinal);

        var card = ApprovalCard.FromJson(result.GateCard!);
        Assert.NotNull(card);
        Assert.Equal(approval.Id, card.ApprovalId);
        Assert.Equal(session.SessionId, card.SessionId);
        Assert.Equal(EchoToolsManifest.WriteTool, card.Tool);
        Assert.Equal(ApprovalStatus.Pending, card.Status);
        Assert.Equal(approval.ExpiresAt, card.ExpiresAt);
        Assert.NotEqual(default, card.CreatedAt);
        Assert.Equal("***", card.ArgsMasked!["email"]!.GetValue<string>());

        // Заявка находится по сессии (нужно кабинету/оператору).
        Assert.Equal(approval.Id, harness.FindPendingApproval(session.SessionId)!.Id);
    }

    [Fact]
    public async Task Approved_decision_after_service_restart_invokes_tool_with_original_arguments()
    {
        using var harness = new RuntimeHarness(EchoAgentConfig.Create(), new ScriptedChatClient());
        harness.ChatClient.EnqueueToolCall(
            EchoToolsManifest.WriteTool,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["message"] = "после рестарта",
                ["email"] = "client@example.com"
            });

        var session = harness.CreateSession();

        // Процесс #1: пауза, заявка и чекпоинт записаны в БД.
        var firstContext = harness.CreateContext();
        var paused = await harness
            .CreateRuntime(firstContext)
            .RunAsync(session, harness.CreateMessage(session, "Запиши"), CancellationToken.None);
        var approvalId = paused.ApprovalId!.Value;
        await firstContext.DisposeAsync();

        // Процесс #2: новый Runtime, кеш и DbContext — заявка читается из БД.
        harness.ChatClient.EnqueueReply("Записал после рестарта");
        await harness
            .CreateRuntime()
            .ResumeAsync(
                session,
                new Decision(ApprovalStatus.Approved, ApprovalDecisionKind.Operator, Comment: "ок"));

        // Тул исполнен с полными аргументами из чекпоинта, а не с маскированными.
        var request = Assert.Single(harness.ToolInvoker.Requests);
        Assert.Equal(EchoToolsManifest.WriteTool, request.ToolName);
        Assert.Contains("client@example.com", request.ArgumentsJson, StringComparison.Ordinal);

        var approval = harness.FindApproval(approvalId);
        Assert.NotNull(approval);
        Assert.Equal(ApprovalStatus.Approved, approval.Status);
        Assert.Equal(ApprovalDecisionKind.Operator, approval.DecidedByKind);
        Assert.NotNull(approval.DecidedAt);

        var state = harness.LatestState(session.SessionId);
        Assert.Null(state.PendingGate);
        Assert.Equal("Записал после рестарта", state.Messages[^1].Content);
    }

    [Fact]
    public async Task Rejected_decision_after_restart_continues_dialog_without_tool_call()
    {
        using var harness = new RuntimeHarness(EchoAgentConfig.Create(), new ScriptedChatClient());
        harness.ChatClient.EnqueueToolCall(EchoToolsManifest.WriteTool);

        var session = harness.CreateSession();
        var paused = await harness
            .CreateRuntime()
            .RunAsync(session, harness.CreateMessage(session, "Запиши"), CancellationToken.None);

        harness.ChatClient.EnqueueReply("Хорошо, действие отменено.");
        await harness
            .CreateRuntime()
            .ResumeAsync(session, new Decision(ApprovalStatus.Rejected, ApprovalDecisionKind.User));

        Assert.Empty(harness.ToolInvoker.Requests);

        var approval = harness.FindApproval(paused.ApprovalId!.Value);
        Assert.NotNull(approval);
        Assert.Equal(ApprovalStatus.Rejected, approval.Status);
        Assert.Equal(ApprovalDecisionKind.User, approval.DecidedByKind);

        var state = harness.LatestState(session.SessionId);
        Assert.Null(state.PendingGate);
        Assert.Contains(
            state.Messages,
            message => message.Role == MessageRole.System
                && message.Content.Contains("отклонено", StringComparison.Ordinal));
        Assert.Equal("Хорошо, действие отменено.", state.Messages[^1].Content);
    }

    [Fact]
    public async Task Gate_expires_by_tenant_timeout_and_action_is_not_executed()
    {
        using var harness = new RuntimeHarness(EchoAgentConfig.Create(), new ScriptedChatClient());
        harness.Options.GateExpiry = TimeSpan.FromMilliseconds(50);
        harness.ChatClient.EnqueueToolCall(EchoToolsManifest.WriteTool);

        var session = harness.CreateSession();
        var paused = await harness
            .CreateRuntime()
            .RunAsync(session, harness.CreateMessage(session, "Запиши"), CancellationToken.None);

        await Task.Delay(150);

        // Подтверждение пришло после таймаута: решение трактуется как expired.
        harness.ChatClient.EnqueueReply("Подтверждение истекло, действие не выполнено.");
        await harness
            .CreateRuntime()
            .ResumeAsync(session, new Decision(ApprovalStatus.Approved, ApprovalDecisionKind.Operator));

        Assert.Empty(harness.ToolInvoker.Requests);

        var approval = harness.FindApproval(paused.ApprovalId!.Value);
        Assert.NotNull(approval);
        Assert.Equal(ApprovalStatus.Expired, approval.Status);
        Assert.Equal(ApprovalDecisionKind.System, approval.DecidedByKind);

        var state = harness.LatestState(session.SessionId);
        Assert.Null(state.PendingGate);
        Assert.Contains(
            state.Messages,
            message => message.Role == MessageRole.System
                && message.Content.Contains("истекло", StringComparison.Ordinal));
        Assert.Equal("Подтверждение истекло, действие не выполнено.", state.Messages[^1].Content);
    }

    [Fact]
    public async Task Tenant_gate_timeout_override_is_applied_to_pending_approval()
    {
        using var harness = new RuntimeHarness(EchoAgentConfig.Create(), new ScriptedChatClient());
        harness.Options.GateExpiryOverrides[EchoAgentConfig.TenantId] = TimeSpan.FromHours(2);
        harness.ChatClient.EnqueueToolCall(EchoToolsManifest.WriteTool);

        var session = harness.CreateSession();
        var before = DateTimeOffset.UtcNow;
        var paused = await harness
            .CreateRuntime()
            .RunAsync(session, harness.CreateMessage(session, "Запиши"), CancellationToken.None);

        var approval = harness.FindApproval(paused.ApprovalId!.Value);
        Assert.NotNull(approval);
        Assert.InRange(
            approval.ExpiresAt,
            before.AddHours(2) - ClockTolerance,
            before.AddHours(2) + ClockTolerance);
        Assert.Equal(approval.ExpiresAt, harness.LatestState(session.SessionId).PendingGate!.ExpiresAt);
    }

    [Fact]
    public async Task Two_sessions_of_tenant_keep_independent_pending_gates()
    {
        using var harness = new RuntimeHarness(EchoAgentConfig.Create(), new ScriptedChatClient());
        harness.ChatClient
            .EnqueueToolCall(
                EchoToolsManifest.WriteTool,
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["message"] = "сессия A" })
            .EnqueueToolCall(
                EchoToolsManifest.WriteTool,
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["message"] = "сессия B" });

        var sessionA = harness.CreateSession();
        var sessionB = harness.CreateSession();
        var runtime = harness.CreateRuntime();

        var pausedA = await runtime.RunAsync(
            sessionA,
            harness.CreateMessage(sessionA, "Запиши A"),
            CancellationToken.None);
        var pausedB = await runtime.RunAsync(
            sessionB,
            harness.CreateMessage(sessionB, "Запиши B"),
            CancellationToken.None);

        Assert.NotEqual(pausedA.ApprovalId, pausedB.ApprovalId);
        Assert.Equal(pausedA.ApprovalId, harness.FindPendingApproval(sessionA.SessionId)!.Id);
        Assert.Equal(pausedB.ApprovalId, harness.FindPendingApproval(sessionB.SessionId)!.Id);

        // Подтверждение сессии A не трогает гейт сессии B.
        harness.ChatClient.EnqueueReply("A выполнено");
        await harness
            .CreateRuntime()
            .ResumeAsync(sessionA, new Decision(ApprovalStatus.Approved, ApprovalDecisionKind.Operator));

        var request = Assert.Single(harness.ToolInvoker.Requests);
        Assert.Contains("сессия A", request.ArgumentsJson, StringComparison.Ordinal);
        Assert.Equal(ApprovalStatus.Approved, harness.FindApproval(pausedA.ApprovalId!.Value)!.Status);
        Assert.Equal(ApprovalStatus.Pending, harness.FindApproval(pausedB.ApprovalId!.Value)!.Status);
        Assert.NotNull(harness.LatestState(sessionB.SessionId).PendingGate);

        // Отказ в сессии B не исполняет тул и не переисполняет A.
        harness.ChatClient.EnqueueReply("B отменено");
        await runtime.ResumeAsync(sessionB, new Decision(ApprovalStatus.Rejected, ApprovalDecisionKind.User));

        Assert.Single(harness.ToolInvoker.Requests);
        Assert.Equal(ApprovalStatus.Rejected, harness.FindApproval(pausedB.ApprovalId!.Value)!.Status);
        Assert.Null(harness.LatestState(sessionB.SessionId).PendingGate);
        Assert.Equal("B отменено", harness.LatestState(sessionB.SessionId).Messages[^1].Content);
    }

    [Fact]
    public async Task Session_cancelled_while_awaiting_gate_supersedes_approval_without_action()
    {
        using var harness = new RuntimeHarness(EchoAgentConfig.Create(), new ScriptedChatClient());
        harness.ChatClient.EnqueueToolCall(EchoToolsManifest.WriteTool);

        var session = harness.CreateSession();
        var paused = await harness
            .CreateRuntime()
            .RunAsync(session, harness.CreateMessage(session, "Запиши"), CancellationToken.None);

        var cancelled = session with { Status = SessionStatus.Closed };
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.CreateRuntime().ResumeAsync(
                cancelled,
                new Decision(ApprovalStatus.Approved, ApprovalDecisionKind.Operator)));

        Assert.Contains("closed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.ToolInvoker.Requests);

        var approval = harness.FindApproval(paused.ApprovalId!.Value);
        Assert.NotNull(approval);
        Assert.Equal(ApprovalStatus.Superseded, approval.Status);
        Assert.Equal(ApprovalDecisionKind.System, approval.DecidedByKind);

        // Чекпоинт не перезаписан: отменённая сессия не возобновляет диалог.
        Assert.NotNull(harness.LatestState(session.SessionId).PendingGate);
    }

    [Fact]
    public async Task Resume_uses_pinned_agent_version_when_new_version_is_published()
    {
        var configStore = new FakeAgentConfigStore(EchoAgentConfig.Create(version: 1));
        using var harness = new RuntimeHarness(
            EchoAgentConfig.Create(version: 1),
            new ScriptedChatClient(),
            configStore: configStore);

        harness.ChatClient.EnqueueToolCall(
            EchoToolsManifest.WriteTool,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["message"] = "закреплённая версия" });

        var session = harness.CreateSession(agentVersion: 1);
        var paused = await harness
            .CreateRuntime()
            .RunAsync(session, harness.CreateMessage(session, "Запиши"), CancellationToken.None);

        // Между паузой и resume опубликована версия 2, где write-тула уже нет.
        configStore.Set(EchoAgentConfig.WithoutTool(EchoToolsManifest.WriteTool, version: 2));

        harness.ChatClient.EnqueueReply("Выполнено на закреплённой версии");
        await harness
            .CreateRuntime()
            .ResumeAsync(session, new Decision(ApprovalStatus.Approved, ApprovalDecisionKind.Operator));

        // Resume работает на версии 1 (version pinning), а не на current_version.
        var request = Assert.Single(harness.ToolInvoker.Requests);
        Assert.Equal(EchoToolsManifest.WriteTool, request.ToolName);
        Assert.Equal(ApprovalStatus.Approved, harness.FindApproval(paused.ApprovalId!.Value)!.Status);
        Assert.Equal(
            "Выполнено на закреплённой версии",
            harness.LatestState(session.SessionId).Messages[^1].Content);
    }
}
