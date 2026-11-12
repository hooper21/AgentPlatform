using System.Text.Json;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Json;
using StackExchange.Redis;

namespace AgentPlatform.Runtime;

/// <summary>
/// Кеш конфигурации агента в Redis с TTL 60 с (мультиинстансовый Runtime).
/// <para>
/// Инвалидация — инкремент «поколения» ключа агента: старые записи больше не читаются
/// и уходят из Redis по TTL. Redis не обязателен: если он недоступен, кеш просто
/// не используется и конфигурация грузится из БД.
/// </para>
/// </summary>
public sealed class RedisAgentConfigCache : IAgentConfigCache
{
    private const string KeyPrefix = "agentplatform:agent-config";

    private readonly Lazy<IConnectionMultiplexer?> _connection;
    private readonly TimeSpan _ttl;

    /// <summary>Создаёт кеш по строке подключения (соединение ленивое).</summary>
    public RedisAgentConfigCache(string connectionString, TimeSpan ttl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be positive.");
        }

        _ttl = ttl;
        _connection = new Lazy<IConnectionMultiplexer?>(
            () => TryConnect(connectionString),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public async Task<AgentRuntimeConfig?> GetAsync(
        Guid tenantId,
        Guid agentId,
        int agentVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var database = GetDatabase();
        if (database is null)
        {
            return null;
        }

        try
        {
            var generation = await database
                .StringGetAsync(GenerationKey(tenantId, agentId))
                .ConfigureAwait(false);

            var json = await database
                .StringGetAsync(ConfigKey(tenantId, agentId, agentVersion, generation))
                .ConfigureAwait(false);

            if (json.IsNullOrEmpty)
            {
                return null;
            }

            return PlatformJson.DeserializeDb<AgentRuntimeConfig>(json.ToString());
        }
        catch (RedisException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (JsonException)
        {
            // Повреждённая запись — промах кеша, конфигурация будет перечитана из БД.
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(AgentRuntimeConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        cancellationToken.ThrowIfCancellationRequested();

        var database = GetDatabase();
        if (database is null)
        {
            return;
        }

        try
        {
            var generation = await database
                .StringGetAsync(GenerationKey(config.TenantId, config.AgentId))
                .ConfigureAwait(false);

            await database
                .StringSetAsync(
                    ConfigKey(config.TenantId, config.AgentId, config.Version, generation),
                    PlatformJson.SerializeDb(config),
                    _ttl)
                .ConfigureAwait(false);
        }
        catch (RedisException)
        {
            // Кеш недоступен — работаем без него.
        }
        catch (TimeoutException)
        {
        }
    }

    /// <inheritdoc />
    public async Task InvalidateAsync(Guid tenantId, Guid agentId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var database = GetDatabase();
        if (database is null)
        {
            return;
        }

        try
        {
            await database
                .StringIncrementAsync(GenerationKey(tenantId, agentId))
                .ConfigureAwait(false);
        }
        catch (RedisException)
        {
        }
        catch (TimeoutException)
        {
        }
    }

    private IDatabase? GetDatabase() => _connection.Value?.GetDatabase();

    private static string GenerationKey(Guid tenantId, Guid agentId)
        => $"{KeyPrefix}:generation:{tenantId:N}:{agentId:N}";

    private static string ConfigKey(Guid tenantId, Guid agentId, int agentVersion, RedisValue generation)
        => $"{KeyPrefix}:{tenantId:N}:{agentId:N}:v{agentVersion}:g{(generation.IsNull ? "0" : generation.ToString())}";

    private static IConnectionMultiplexer? TryConnect(string connectionString)
    {
        try
        {
            var options = ConfigurationOptions.Parse(connectionString);

            // Redis — кеш: при старте он не обязателен, переподключение идёт в фоне.
            options.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(options);
        }
        catch (RedisException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
