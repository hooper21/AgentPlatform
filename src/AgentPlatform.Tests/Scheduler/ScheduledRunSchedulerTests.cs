using AgentPlatform.Core.Enums;
using AgentPlatform.Scheduler.Scheduling;
using AgentPlatform.Tests.Runtime;
using Microsoft.Extensions.AI;

namespace AgentPlatform.Tests.Scheduler;

/// <summary>
/// End-to-end тест Scheduler: cron «каждую минуту» запускает тестового агента
/// без входящего сообщения, результат пишется в messages сессии.
/// </summary>
public sealed class ScheduledRunSchedulerTests
{
    [Fact]
    public async Task Every_minute_cron_launches_agent_and_writes_messages()
    {
        var chat = new ScriptedChatClient().EnqueueReply("Плановый отчёт готов");
        using var harness = new SchedulerHarness(chat, SchedulerHarness.Start.AddSeconds(30));

        // До наступления минуты запуска нет.
        Assert.Equal(0, await harness.Runner.RunOnceAsync(CancellationToken.None));
        Assert.Empty(harness.ReadSessions());

        harness.Time.UtcNow = SchedulerHarness.Start.AddMinutes(1).AddSeconds(5);
        Assert.Equal(1, await harness.Runner.RunOnceAsync(CancellationToken.None));

        var session = Assert.Single(harness.ReadSessions());
        Assert.Equal(SchedulerHarness.TenantId, session.TenantId);
        Assert.Equal(SchedulerHarness.AgentId, session.AgentId);
        Assert.Equal(EchoAgentConfig.Version, session.AgentVersion);
        Assert.Equal(SessionStatus.Active.ToDbValue(), session.Status);
        Assert.StartsWith("scheduler:", session.UserRef, StringComparison.Ordinal);

        // Плановый запуск получает служебный канал: входящего сообщения визитера нет.
        var channel = Assert.Single(harness.ReadChannels());
        Assert.Equal(ScheduledRunExecutor.SystemChannelType, channel.Type);
        Assert.Equal(channel.Id, session.ChannelId);

        // Результат запуска: системный триггер и ответ агента в журнале сессии.
        var messages = harness.ReadMessages();
        Assert.Equal(2, messages.Count);
        Assert.Equal(MessageRole.System.ToDbValue(), messages[0].Role);
        Assert.Equal(ScheduledTrigger.Text, messages[0].Content);
        Assert.Equal(MessageRole.Assistant.ToDbValue(), messages[1].Role);
        Assert.Equal("Плановый отчёт готов", messages[1].Content);
        Assert.All(messages, message => Assert.Equal(session.Id, message.SessionId));

        var run = harness.ReadScheduledRun();
        Assert.Equal(harness.Time.UtcNow, run.LastRunAt);
        Assert.Equal(ScheduledRunStatus.Ok.ToDbValue(), run.LastRunStatus);

        // Runtime получил именно системный триггер.
        var history = Assert.Single(harness.ChatClient.Calls);
        Assert.Contains(
            history,
            message => message.Role == ChatRole.User && message.Text == ScheduledTrigger.Text);

        // Следующий тик до новой минуты повторный запуск не делает.
        harness.Time.UtcNow = SchedulerHarness.Start.AddMinutes(1).AddSeconds(30);
        Assert.Equal(0, await harness.Runner.RunOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Each_occurrence_creates_new_session_and_messages()
    {
        var chat = new ScriptedChatClient()
            .EnqueueReply("Отчёт 1")
            .EnqueueReply("Отчёт 2");

        using var harness = new SchedulerHarness(chat, SchedulerHarness.Start.AddMinutes(1).AddSeconds(5));

        Assert.Equal(1, await harness.Runner.RunOnceAsync(CancellationToken.None));

        harness.Time.UtcNow = SchedulerHarness.Start.AddMinutes(2).AddSeconds(5);
        Assert.Equal(1, await harness.Runner.RunOnceAsync(CancellationToken.None));

        Assert.Equal(2, harness.ReadSessions().Count);

        var messages = harness.ReadMessages();
        Assert.Equal(4, messages.Count);
        Assert.Equal(2, messages.Count(message => message.Content == ScheduledTrigger.Text));
        Assert.Contains(messages, message => message.Content == "Отчёт 1");
        Assert.Contains(messages, message => message.Content == "Отчёт 2");
    }

    [Fact]
    public async Task Missing_agent_marks_scheduled_run_as_error()
    {
        var chat = new ScriptedChatClient();
        using var harness = new SchedulerHarness(
            chat,
            SchedulerHarness.Start.AddMinutes(1).AddSeconds(5),
            seedAgent: false);

        Assert.Equal(1, await harness.Runner.RunOnceAsync(CancellationToken.None));

        Assert.Empty(harness.ReadSessions());
        Assert.Empty(harness.ReadMessages());

        var run = harness.ReadScheduledRun();
        Assert.Equal(ScheduledRunStatus.Error.ToDbValue(), run.LastRunStatus);
        Assert.NotNull(run.LastRunAt);
    }
}
