using System.Collections.Concurrent;

namespace AgentPlatform.Gateway.RateLimiting;

/// <summary>
/// Rate-limit тенанта в памяти процесса — та же семантика token bucket, что и у
/// Redis-реализации. Используется для локальной разработки и тестов; в
/// мультиинстансовой установке счётчики не разделяются (см. <see cref="RateLimitOptions.Storage"/>).
/// </summary>
public sealed class InMemoryTenantRateLimiter : ITenantRateLimiter
{
    private readonly ConcurrentDictionary<Guid, Bucket> _buckets = new();
    private readonly RateLimitOptions _options;

    /// <summary>Создаёт лимитер по настройкам тенанта.</summary>
    public InMemoryTenantRateLimiter(RateLimitOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    /// <inheritdoc />
    public Task<RateLimitDecision> AcquireAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("tenant_id is required", nameof(tenantId));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;
        var bucket = _buckets.GetOrAdd(tenantId, _ => new Bucket(_options.Capacity, now));

        lock (bucket.Gate)
        {
            var elapsedSeconds = Math.Max(0, (now - bucket.UpdatedAt).TotalSeconds);
            bucket.Tokens = Math.Min(_options.Capacity, bucket.Tokens + (elapsedSeconds * _options.TokensPerSecond));
            bucket.UpdatedAt = now;

            if (bucket.Tokens >= _options.Cost)
            {
                bucket.Tokens -= _options.Cost;

                return Task.FromResult(new RateLimitDecision(
                    Allowed: true,
                    Remaining: (int)Math.Floor(bucket.Tokens),
                    RetryAfter: TimeSpan.Zero));
            }

            var wait = TimeSpan.FromSeconds((_options.Cost - bucket.Tokens) / _options.TokensPerSecond);

            return Task.FromResult(new RateLimitDecision(
                Allowed: false,
                Remaining: (int)Math.Floor(bucket.Tokens),
                RetryAfter: wait));
        }
    }

    /// <summary>Ведро тенанта: токены и момент последнего пополнения.</summary>
    private sealed class Bucket(double capacity, DateTimeOffset now)
    {
        public object Gate { get; } = new();

        public double Tokens { get; set; } = capacity;

        public DateTimeOffset UpdatedAt { get; set; } = now;
    }
}
