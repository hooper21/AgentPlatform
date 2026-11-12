using AgentPlatform.Core.Abstractions;
using AgentPlatform.Gateway.Connectors;
using AgentPlatform.Gateway.RateLimiting;
using AgentPlatform.Gateway.Resilience;
using AgentPlatform.Gateway.Security;
using AgentPlatform.Gateway.Sessions;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Repositories;
using AgentPlatform.LlmGateway;

namespace AgentPlatform.Gateway.Composition;

/// <summary>
/// Сборка Gateway в DI: endpoint-ы получают готовые сервисы, тесты используют те же
/// регистрации, подменяя только внешние адаптеры (LLM-клиент, IToolInvoker).
/// </summary>
public static class GatewayServiceCollectionExtensions
{
    /// <summary>Gateway: JWT виджета, сессии, журнал диалога, SSE-поток и rate-limit.</summary>
    public static IServiceCollection AddGatewaySessions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(_ => new ReplyStreamHub());
        services.AddSingleton<IWidgetSecretProvider, ConfigurationWidgetSecretProvider>();
        services.AddSingleton<WidgetJwtAuthenticator>();

        services.AddScoped<SessionBinder>();
        services.AddScoped<DialogLog>();
        services.AddScoped<GatewayMessagePipeline>();

        services.AddGatewayRateLimiting(configuration);

        return services;
    }

    /// <summary>
    /// Rate-limit на тенанта: Redis (по умолчанию) или память процесса
    /// (<c>Gateway:RateLimit:Storage=memory</c> — локальная разработка и тесты).
    /// </summary>
    public static IServiceCollection AddGatewayRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new RateLimitOptions();
        configuration.GetSection("Gateway:RateLimit").Bind(options);
        options.Validate();

        services.AddSingleton(options);

        if (string.Equals(options.Storage, "memory", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ITenantRateLimiter, InMemoryTenantRateLimiter>();
        }
        else
        {
            services.AddSingleton<ITenantRateLimiter, RedisTenantRateLimiter>();
        }

        return services;
    }

    /// <summary>
    /// Повторы вызова LLM. Фабрика оборачивает LLM Gateway: пока реальный провайдер
    /// не подключён, используется <see cref="StubChatClientFactory"/> (TODO 2.x contract).
    /// </summary>
    public static IServiceCollection AddGatewayLlmResilience(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new LlmResilienceOptions();
        configuration.GetSection("Gateway:Llm").Bind(options);
        options.Validate();

        services.AddSingleton(options);

        services.AddSingleton<IChatClientFactory>(serviceProvider => new RetryingChatClientFactory(
            new StubChatClientFactory(),
            serviceProvider.GetRequiredService<LlmResilienceOptions>(),
            serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<RetryingChatClient>()));

        return services;
    }

    /// <summary>
    /// Порты Runtime, реализации которых живут в Infrastructure/Connector Host:
    /// конфигурация агента, чекпоинты, заявки HITL и вызов тулов.
    /// </summary>
    public static IServiceCollection AddGatewayRuntimePorts(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IAgentConfigStore>(serviceProvider =>
            new EfAgentConfigStore(serviceProvider.GetRequiredService<AgentPlatformDbContext>()));

        services.AddScoped<ICheckpointStore>(serviceProvider =>
            new EfCheckpointStore(serviceProvider.GetRequiredService<AgentPlatformDbContext>()));

        services.AddScoped<IApprovalStore>(serviceProvider =>
            new EfApprovalStore(serviceProvider.GetRequiredService<AgentPlatformDbContext>()));

        services.AddScoped<IToolInvoker, UnavailableConnectorToolInvoker>();

        return services;
    }
}
