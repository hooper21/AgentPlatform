using AgentPlatform.Core.Abstractions;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Repositories;
using AgentPlatform.Infrastructure.Tenancy;
using AgentPlatform.Scheduler.Composition;
using AgentPlatform.Scheduler.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentPlatform.Tests.Scheduler;

/// <summary>
/// Проверка DI-сборки консольного Scheduler: порты Runtime, LLM Gateway,
/// cron-исполнитель и hosted service разрешаются без внешней PostgreSQL.
/// </summary>
public sealed class SchedulerCompositionTests
{
    [Fact]
    public void Scheduler_di_graph_resolves()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddDbContext<AgentPlatformDbContext>(options =>
            options.UseInMemoryDatabase($"agentplatform-scheduler-di-{Guid.NewGuid():N}"));

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(provider => provider.GetRequiredService<TenantContext>());
        services.AddScoped(typeof(ITenantScopedRepository<>), typeof(TenantScopedRepository<>));
        services.AddScoped(typeof(IGlobalRepository<>), typeof(GlobalRepository<>));
        services.AddScoped<IScheduledRunStore, EfScheduledRunStore>();

        services.AddSchedulerRuntime(configuration);
        services.AddHostedService<AgentPlatform.Scheduler.SchedulerWorker>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ScheduledRunExecutor>());
        Assert.Contains(
            scope.ServiceProvider.GetServices<IHostedService>(),
            service => service is AgentPlatform.Scheduler.SchedulerWorker);
    }
}
