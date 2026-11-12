using AgentPlatform.Controller.Contracts;
using AgentPlatform.Controller.Dialogs;
using AgentPlatform.ConnectorHost.Tools;
using AgentPlatform.Core.Enums;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Persistence.Entities;
using AgentPlatform.Infrastructure.Repositories;
using AgentPlatform.Infrastructure.Tenancy;
using AgentPlatform.Tests.Runtime;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Tests.Controller;

/// <summary>
/// Сквозной сценарий кабинета (гейт-ревью фазы 1): сообщение → echo_write → пауза →
/// подтверждение оператора → ResumeAsync → исполнение. Между паузой и решением
/// создаётся новый DbContext/Runtime/сервис — эмуляция рестарта Controller.
/// </summary>
public sealed class CabinetDialogTests
{
    private static readonly Guid OperatorId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    [Fact]
    public async Task Write_tool_pauses_and_approval_after_restart_executes_tool()
    {
        var chat = new ScriptedChatClient()
            .EnqueueToolCall(
                EchoToolsManifest.WriteTool,
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["message"] = "отправить счёт" })
            .EnqueueReply("Готово: echo(write): отправить счёт");

        using var harness = new RuntimeHarness(EchoAgentConfig.Create(), chat);

        // Процесс #1: первый ход останавливается на гейте.
        var firstContext = harness.CreateContext();
        await SeedAgentAsync(firstContext);

        var firstService = CreateService(harness, firstContext);
        var paused = await firstService.StartAsync(
            EchoAgentConfig.TenantId,
            new CabinetDialogStartRequest(EchoAgentConfig.AgentId, "write: отправить счёт", "cabinet"));

        Assert.Equal("awaiting_gate", paused.Status);
        Assert.NotNull(paused.PendingApproval);
        Assert.Equal(EchoToolsManifest.WriteTool, paused.PendingApproval!.Tool);
        Assert.Equal(ApprovalStatus.Pending.ToDbValue(), paused.PendingApproval.Status);
        Assert.DoesNotContain("отправить счёт", paused.PendingApproval.ArgsMasked, StringComparison.Ordinal);
        Assert.Empty(harness.ToolInvoker.Requests);

        // В журнале диалога только сообщение пользователя: ответа ещё нет.
        Assert.Single(paused.Messages);
        Assert.Equal(MessageRole.User.ToDbValue(), paused.Messages[0].Role);

        await firstContext.DisposeAsync();

        // Процесс #2: новый DbContext, новый Runtime, новый экземпляр сервиса.
        var secondContext = harness.CreateContext();
        var secondService = CreateService(harness, secondContext);

        var decided = await secondService.DecideAsync(
            EchoAgentConfig.TenantId,
            paused.SessionId,
            paused.PendingApproval.ApprovalId,
            new CabinetApprovalDecisionRequest("approve", "ok", ActorId: OperatorId));

        Assert.NotNull(decided);
        Assert.Equal("active", decided!.Status);
        Assert.Null(decided.PendingApproval);

        var request = Assert.Single(harness.ToolInvoker.Requests);
        Assert.Equal(EchoToolsManifest.WriteTool, request.ToolName);
        Assert.Contains("отправить счёт", request.ArgumentsJson, StringComparison.Ordinal);

        var approval = harness.FindApproval(paused.PendingApproval.ApprovalId);
        Assert.NotNull(approval);
        Assert.Equal(ApprovalStatus.Approved, approval!.Status);
        Assert.Equal(ApprovalDecisionKind.Operator, approval.DecidedByKind);

        // В журнал перенесены tool/assistant сообщения из чекпоинта resume.
        Assert.Contains(decided.Messages, message =>
            message.Role == MessageRole.Tool.ToDbValue()
            && message.Content.Contains("echo(write): отправить счёт", StringComparison.Ordinal));
        Assert.Contains(decided.Messages, message =>
            message.Role == MessageRole.Assistant.ToDbValue()
            && message.Content.Contains("Готово", StringComparison.Ordinal));

        await secondContext.DisposeAsync();
    }

    [Fact]
    public async Task Rejected_gate_does_not_execute_tool_and_dialog_continues()
    {
        var chat = new ScriptedChatClient()
            .EnqueueToolCall(EchoToolsManifest.WriteTool)
            .EnqueueReply("Запись отменена оператором.");

        using var harness = new RuntimeHarness(EchoAgentConfig.Create(), chat);
        var context = harness.CreateContext();
        await SeedAgentAsync(context);

        var service = CreateService(harness, context);
        var paused = await service.StartAsync(
            EchoAgentConfig.TenantId,
            new CabinetDialogStartRequest(EchoAgentConfig.AgentId, "write: тест", "cabinet"));

        Assert.NotNull(paused.PendingApproval);

        var decided = await service.DecideAsync(
            EchoAgentConfig.TenantId,
            paused.SessionId,
            paused.PendingApproval!.ApprovalId,
            new CabinetApprovalDecisionRequest("reject", "нельзя", ActorId: OperatorId));

        Assert.NotNull(decided);
        Assert.Empty(harness.ToolInvoker.Requests);
        Assert.Equal(ApprovalStatus.Rejected, harness.FindApproval(paused.PendingApproval.ApprovalId)!.Status);
        Assert.Contains(decided!.Messages, message =>
            message.Role == MessageRole.System.ToDbValue()
            && message.Content.Contains("отклонено", StringComparison.Ordinal));

        await context.DisposeAsync();
    }

    private static CabinetDialogService CreateService(RuntimeHarness harness, AgentPlatformDbContext context)
    {
        var tenantContext = new TenantContext();
        var runtime = harness.CreateRuntime(context);

        return new CabinetDialogService(
            context,
            tenantContext,
            runtime,
            new EfApprovalStore(context),
            new EfCheckpointStore(context),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CabinetDialogService>.Instance);
    }

    private static async Task SeedAgentAsync(AgentPlatformDbContext context)
    {
        context.Tenants.Add(new Tenant
        {
            Id = EchoAgentConfig.TenantId,
            Name = "tenant",
            Plan = "pilot",
            LlmConfig = "{}",
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        context.Users.Add(new User
        {
            Id = OperatorId,
            TenantId = EchoAgentConfig.TenantId,
            Email = "operator@demo.local",
            PasswordHash = "not-used-in-this-test",
            Role = UserRole.Operator.ToDbValue(),
            AuthMeta = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        context.Agents.Add(new Agent
        {
            Id = EchoAgentConfig.AgentId,
            TenantId = EchoAgentConfig.TenantId,
            Name = "echo-agent",
            CurrentVersion = EchoAgentConfig.Version,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        context.AgentVersions.Add(new AgentVersion
        {
            AgentId = EchoAgentConfig.AgentId,
            Version = EchoAgentConfig.Version,
            Status = "published",
            SystemPrompt = EchoAgentConfig.SystemPrompt,
            ModelRef = """{"model":"test-model","routing":"simple"}""",
            EnabledTools = "[]",
            Rules = "{}",
            EscalationRules = "{}",
            Limits = """{"max_turns":10,"max_tool_calls":8}""",
            CreatedAt = DateTimeOffset.UtcNow
        });

        await context.SaveChangesAsync();
    }
}
