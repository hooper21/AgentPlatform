namespace AgentPlatform.Gateway.RateLimiting;

/// <summary>
/// Настройки rate-limit тенанта (секция <c>Gateway:RateLimit</c>).
/// Используется алгоритм token bucket: ёмкость — burst, пополнение — запросов в секунду.
/// </summary>
public sealed class RateLimitOptions
{
    /// <summary>
    /// Хранилище счётчиков: redis (по умолчанию, мультиинстансовый) | memory
    /// (локальная разработка и тесты).
    /// </summary>
    public string Storage { get; set; } = "redis";

    /// <summary>Ёмкость ведра: сколько запросов можно отправить залпом.</summary>
    public double Capacity { get; set; } = 30;

    /// <summary>Скорость пополнения ведра, запросов в секунду.</summary>
    public double TokensPerSecond { get; set; } = 2;

    /// <summary>Стоимость одного сообщения в токенах.</summary>
    public double Cost { get; set; } = 1;

    /// <summary>TTL ключа в Redis: неактивное ведро исчезает само.</summary>
    public TimeSpan KeyTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Префикс ключей rate-limit в Redis.</summary>
    public string KeyPrefix { get; set; } = "agentplatform:rate-limit:tenant:";

    /// <summary>Проверяет, что настройки пригодны к применению.</summary>
    public void Validate()
    {
        if (Capacity <= 0)
        {
            throw new InvalidOperationException("Gateway:RateLimit:Capacity must be positive.");
        }

        if (TokensPerSecond <= 0)
        {
            throw new InvalidOperationException("Gateway:RateLimit:TokensPerSecond must be positive.");
        }

        if (Cost <= 0 || Cost > Capacity)
        {
            throw new InvalidOperationException(
                "Gateway:RateLimit:Cost must be positive and not greater than Capacity.");
        }

        if (KeyTtl <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Gateway:RateLimit:KeyTtl must be positive.");
        }

        if (string.IsNullOrWhiteSpace(KeyPrefix))
        {
            throw new InvalidOperationException("Gateway:RateLimit:KeyPrefix must not be empty.");
        }
    }
}
