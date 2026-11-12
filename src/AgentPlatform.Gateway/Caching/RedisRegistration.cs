using StackExchange.Redis;

namespace AgentPlatform.Gateway.Caching;

/// <summary>
/// Регистрация Redis (кеши, очереди, rate-limit — задачи фазы 3).
/// Соединение создаётся лениво: при старте сервиса Redis не обязателен.
/// </summary>
public static class RedisRegistration
{
    /// <summary>Добавляет <see cref="IConnectionMultiplexer"/> как singleton.</summary>
    public static IServiceCollection AddPlatformRedis(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            // Строка подключения — из env (Redis__ConnectionString), не секрет, но и не в коде.
            var connectionString = configuration["Redis:ConnectionString"] ?? "localhost:6379";
            var options = ConfigurationOptions.Parse(connectionString);
            options.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(options);
        });

        return services;
    }
}
