using StackExchange.Redis;

namespace AgentPlatform.Gateway.RateLimiting;

/// <summary>
/// Rate-limit тенанта в Redis (token bucket). Скрипт Lua выполняется атомарно:
/// чтение ведра, пополнение по времени, списание токена и запись — одна операция,
/// поэтому несколько реплик Gateway видят согласованный лимит.
/// <para>
/// Redis не обязателен: при недоступности хранилища лимит деградирует в fail-open
/// (запрос пропускается, предупреждение пишется в лог). Требование задачи — не
/// терять диалоги визитеров из-за инфраструктуры лимитов.
/// </para>
/// </summary>
public sealed class RedisTenantRateLimiter : ITenantRateLimiter
{
    /// <summary>
    /// Lua-скрипт ведра. KEYS[1] — ключ тенанта; ARGV: capacity, refill (в секунду),
    /// now (unix ms), cost, ttl (ms). Возвращает {allowed, tokens, retry_ms}.
    /// </summary>
    internal const string TokenBucketScript = """
        local key = KEYS[1]
        local capacity = tonumber(ARGV[1])
        local refill = tonumber(ARGV[2])
        local now = tonumber(ARGV[3])
        local cost = tonumber(ARGV[4])
        local ttl = tonumber(ARGV[5])

        local current = redis.call('HMGET', key, 'tokens', 'ts')
        local tokens = tonumber(current[1])
        local stamped = tonumber(current[2])

        if tokens == nil then
            tokens = capacity
            stamped = now
        end

        tokens = math.min(capacity, tokens + math.max(0, now - stamped) / 1000.0 * refill)

        local allowed = 0
        if tokens >= cost then
            tokens = tokens - cost
            allowed = 1
        end

        redis.call('HSET', key, 'tokens', tokens, 'ts', now)
        redis.call('PEXPIRE', key, ttl)

        local retry = 0
        if allowed == 0 then
            retry = math.ceil((cost - tokens) / refill * 1000.0)
        end

        return { allowed, math.floor(tokens), retry }
        """;

    private readonly IConnectionMultiplexer _connection;
    private readonly RateLimitOptions _options;
    private readonly ILogger<RedisTenantRateLimiter> _logger;

    /// <summary>Создаёт лимитер поверх соединения Redis.</summary>
    public RedisTenantRateLimiter(
        IConnectionMultiplexer connection,
        RateLimitOptions options,
        ILogger<RedisTenantRateLimiter> logger)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _options.Validate();
    }

    /// <inheritdoc />
    public async Task<RateLimitDecision> AcquireAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("tenant_id is required", nameof(tenantId));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var keys = new RedisKey[] { _options.KeyPrefix + tenantId.ToString("N") };
        var values = new RedisValue[]
        {
            _options.Capacity,
            _options.TokensPerSecond,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            _options.Cost,
            (long)_options.KeyTtl.TotalMilliseconds
        };

        try
        {
            var reply = (RedisResult[]?)await _connection
                .GetDatabase()
                .ScriptEvaluateAsync(TokenBucketScript, keys, values)
                .ConfigureAwait(false);

            if (reply is not { Length: 3 })
            {
                _logger.LogWarning(
                    "Rate-limit script returned an unexpected reply for tenant {TenantId}; request is allowed",
                    tenantId);

                return RateLimitDecision.Allow;
            }

            return new RateLimitDecision(
                Allowed: (long)reply[0] == 1,
                Remaining: (int)(long)reply[1],
                RetryAfter: TimeSpan.FromMilliseconds(Math.Max(0, (long)reply[2])));
        }
        catch (RedisException exception)
        {
            _logger.LogWarning(
                exception,
                "Redis rate-limit is unavailable for tenant {TenantId}; request is allowed (fail-open)",
                tenantId);

            return RateLimitDecision.Allow;
        }
        catch (TimeoutException exception)
        {
            _logger.LogWarning(
                exception,
                "Redis rate-limit timed out for tenant {TenantId}; request is allowed (fail-open)",
                tenantId);

            return RateLimitDecision.Allow;
        }
    }
}
