using AgentPlatform.Gateway.RateLimiting;

namespace AgentPlatform.Tests.Gateway;

/// <summary>
/// Token bucket rate-limit тенанта: залп до ёмкости, отказ с Retry-After,
/// независимые ведра тенантов, пополнение во времени.
/// </summary>
public sealed class InMemoryTenantRateLimiterTests
{
    [Fact]
    public async Task Allows_capacity_then_rejects_with_retry_after()
    {
        var limiter = new InMemoryTenantRateLimiter(new RateLimitOptions
        {
            Capacity = 2,
            TokensPerSecond = 0.0001,
            Cost = 1
        });

        var tenantId = Guid.NewGuid();

        Assert.True((await limiter.AcquireAsync(tenantId)).Allowed);
        Assert.True((await limiter.AcquireAsync(tenantId)).Allowed);

        var rejected = await limiter.AcquireAsync(tenantId);

        Assert.False(rejected.Allowed);
        Assert.Equal(0, rejected.Remaining);
        Assert.True(rejected.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public async Task Tenants_have_independent_buckets()
    {
        var limiter = new InMemoryTenantRateLimiter(new RateLimitOptions
        {
            Capacity = 1,
            TokensPerSecond = 0.0001,
            Cost = 1
        });

        var firstTenant = Guid.NewGuid();
        var secondTenant = Guid.NewGuid();

        Assert.True((await limiter.AcquireAsync(firstTenant)).Allowed);
        Assert.False((await limiter.AcquireAsync(firstTenant)).Allowed);
        Assert.True((await limiter.AcquireAsync(secondTenant)).Allowed);
    }

    [Fact]
    public async Task Bucket_is_refilled_over_time()
    {
        var limiter = new InMemoryTenantRateLimiter(new RateLimitOptions
        {
            Capacity = 1,
            TokensPerSecond = 500,
            Cost = 1
        });

        var tenantId = Guid.NewGuid();

        Assert.True((await limiter.AcquireAsync(tenantId)).Allowed);

        await Task.Delay(20);

        Assert.True((await limiter.AcquireAsync(tenantId)).Allowed);
    }

    [Fact]
    public void Invalid_settings_are_rejected_on_construction()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new InMemoryTenantRateLimiter(
            new RateLimitOptions
            {
                Capacity = 1,
                TokensPerSecond = 1,
                Cost = 5
            }));

        Assert.Contains("Cost", exception.Message, StringComparison.Ordinal);
    }
}
