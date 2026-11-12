using AgentPlatform.Core.Abstractions;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Repositories;
using AgentPlatform.LlmGateway;
using AgentPlatform.Runtime;
using AgentPlatform.Scheduler.Connectors;
using AgentPlatform.Scheduler.Scheduling;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPlatform.Scheduler.Composition;

/// <summary>
/// Сборка консольного Scheduler: EF-порты Runtime, LLM Gateway, cron-исполнитель
/// и фоновый worker. Scheduler не принимает входящих сообщений и не имеет UI.
/// </summary>
public static class SchedulerServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует всё, что нужно для планового запуска агентов:
    /// <see cref="IOrchestrationRuntime"/>, хранилища портов и исполнитель расписаний.
    /// </summary>
    public static IServiceCollection AddSchedulerRuntime(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<TimeProvider>(TimeProvider.System);

        // Порты Runtime: реализации в Infrastructure (EF Core / PostgreSQL).
        services.AddScoped<IAgentConfigStore>(serviceProvider =>
            new EfAgentConfigStore(serviceProvider.GetRequiredService<AgentPlatformDbContext>()));

        services.AddScoped<ICheckpointStore>(serviceProvider =>
            new EfCheckpointStore(serviceProvider.GetRequiredService<AgentPlatformDbContext>()));

        services.AddScoped<IApprovalStore>(serviceProvider =>
            new EfApprovalStore(serviceProvider.GetRequiredService<AgentPlatformDbContext>()));

        // MCP-транспорт к Connector Host ещё не подключён (TODO 2.2 contract):
        // тулы недоступны, но плановый запуск без тулов работает.
        services.AddScoped<IToolInvoker, UnavailableConnectorToolInvoker>();

        // LLM Gateway: пока заглушка, чтобы Scheduler стартовал без ключей провайдеров.
        // TODO(2.x) contract: реальная фабрика моделей платформы/тенанта.
        services.AddSingleton<IChatClientFactory, StubChatClientFactory>();

        services.AddMafAgentRuntime(new AgentRuntimeOptions
        {
            RedisConnectionString = configuration["Redis:ConnectionString"]
        });

        services.AddScoped<ScheduledRunExecutor>();
        services.AddSingleton<ScheduledRunRunner>();

        return services;
    }
}
