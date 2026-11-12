using AgentPlatform.ConnectorHost.Tools;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Persistence.Entities;
using AgentPlatform.Infrastructure.Repositories;
using AgentPlatform.Infrastructure.Tenancy;
using AgentPlatform.LlmGateway;
using AgentPlatform.Runtime;
using AgentPlatform.Scheduler.Scheduling;
using AgentPlatform.Tests.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPlatform.Tests.Scheduler;

/// <summary>Управляемое время теста: cron-тики не зависят от реальных часов.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; }

    public override DateTimeOffset GetUtcNow() => UtcNow;
}

/// <summary>
/// Стенд Scheduler: одна InMemory-БД, реальный MafOrchestrationRuntime и
/// EfScheduledRunStore. LLM подменена сценарным клиентом, тулы — in-process EchoTools.
/// </summary>
internal sealed class SchedulerHarness : IDisposable
{
    public static readonly Guid TenantId = EchoAgentConfig.TenantId;
    public static readonly Guid AgentId = EchoAgentConfig.AgentId;
    public static readonly Guid ScheduledRunId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly ServiceProvider _provider;
    private readonly string _databaseName = $"agentplatform-scheduler-{Guid.NewGuid():N}";

    public SchedulerHarness(
        ScriptedChatClient chatClient,
        DateTimeOffset? now = null,
        bool seedAgent = true,
        string cronExpression = "* * * * *")
    {
        ChatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        Time = new ManualTimeProvider { UtcNow = now ?? Start };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AgentPlatformDbContext>(options => options
            .UseInMemoryDatabase(_databaseName)
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(provider => provider.GetRequiredService<TenantContext>());
        services.AddScoped(typeof(ITenantScopedRepository<>), typeof(TenantScopedRepository<>));
        services.AddScoped(typeof(IGlobalRepository<>), typeof(GlobalRepository<>));
        services.AddScoped<IScheduledRunStore, EfScheduledRunStore>();

        services.AddSingleton<IAgentConfigStore>(new FakeAgentConfigStore(EchoAgentConfig.Create()));
        services.AddScoped<ICheckpointStore>(provider =>
            new EfCheckpointStore(provider.GetRequiredService<AgentPlatformDbContext>()));
        services.AddScoped<IApprovalStore>(provider =>
            new EfApprovalStore(provider.GetRequiredService<AgentPlatformDbContext>()));
        services.AddSingleton<IToolInvoker>(new EchoToolsInvoker());
        services.AddSingleton<IChatClientFactory>(new SingleChatClientFactory(chatClient));
        services.AddSingleton<TimeProvider>(Time);

        services.AddMafAgentRuntime(new AgentRuntimeOptions());
        services.AddScoped<ScheduledRunExecutor>();
        services.AddSingleton<ScheduledRunRunner>();

        _provider = services.BuildServiceProvider();

        Seed(seedAgent, cronExpression);
    }

    public ScriptedChatClient ChatClient { get; }

    public ManualTimeProvider Time { get; }

    public ScheduledRunRunner Runner => _provider.GetRequiredService<ScheduledRunRunner>();

    public IReadOnlyList<AgentSession> ReadSessions()
    {
        using var scope = _provider.CreateScope();
        return scope.ServiceProvider
            .GetRequiredService<AgentPlatformDbContext>()
            .Sessions.AsNoTracking()
            .OrderBy(session => session.StartedAt)
            .ToList();
    }

    public IReadOnlyList<Message> ReadMessages()
    {
        using var scope = _provider.CreateScope();
        return scope.ServiceProvider
            .GetRequiredService<AgentPlatformDbContext>()
            .Messages.AsNoTracking()
            .OrderBy(message => message.Id)
            .ToList();
    }

    public IReadOnlyList<Channel> ReadChannels()
    {
        using var scope = _provider.CreateScope();
        return scope.ServiceProvider
            .GetRequiredService<AgentPlatformDbContext>()
            .Channels.AsNoTracking()
            .ToList();
    }

    public ScheduledRun ReadScheduledRun()
    {
        using var scope = _provider.CreateScope();
        return scope.ServiceProvider
            .GetRequiredService<AgentPlatformDbContext>()
            .ScheduledRuns.AsNoTracking()
            .Single(run => run.Id == ScheduledRunId);
    }

    public void Dispose() => _provider.Dispose();

    private void Seed(bool seedAgent, string cronExpression)
    {
        using var scope = _provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AgentPlatformDbContext>();
        context.Database.EnsureCreated();

        context.Tenants.Add(new Tenant
        {
            Id = TenantId,
            Name = "scheduler-tenant",
            Plan = "pilot",
            LlmConfig = "{}",
            Status = "active",
            CreatedAt = Start,
            UpdatedAt = Start
        });

        if (seedAgent)
        {
            context.Agents.Add(new Agent
            {
                Id = AgentId,
                TenantId = TenantId,
                Name = "scheduled-agent",
                CurrentVersion = EchoAgentConfig.Version,
                CreatedAt = Start,
                UpdatedAt = Start
            });
        }

        context.ScheduledRuns.Add(new ScheduledRun
        {
            Id = ScheduledRunId,
            TenantId = TenantId,
            AgentId = AgentId,
            CronExpression = cronExpression,
            Enabled = true,
            CreatedAt = Start,
            UpdatedAt = Start
        });

        context.SaveChanges();
    }
}
