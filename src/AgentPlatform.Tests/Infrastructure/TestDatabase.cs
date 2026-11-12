using AgentPlatform.Core.Abstractions;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AgentPlatform.Tests.Infrastructure;

/// <summary>Управляемый ITenantContext для тестов (без HTTP-запроса).</summary>
internal sealed class FakeTenantContext : ITenantContext
{
    public Guid? TenantId { get; set; }

    public bool IsResolved => TenantId.HasValue;

    public static FakeTenantContext For(Guid tenantId) => new() { TenantId = tenantId };
}

/// <summary>
/// Тестовый DbContext на InMemory-провайдере (только для юнит-тестов слоя данных,
/// интеграционные тесты на PostgreSQL — отдельная задача).
/// </summary>
internal sealed class TestDatabase : IDisposable
{
    private TestDatabase(AgentPlatformDbContext context)
    {
        Context = context;
    }

    public AgentPlatformDbContext Context { get; }

    public static TestDatabase Create()
    {
        var options = new DbContextOptionsBuilder<AgentPlatformDbContext>()
            .UseInMemoryDatabase($"agentplatform-tests-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(new AuditingInterceptor())
            .Options;

        var context = new AgentPlatformDbContext(options);
        context.Database.EnsureCreated();

        return new TestDatabase(context);
    }

    public void Dispose() => Context.Dispose();
}
