using AgentPlatform.Infrastructure.Persistence.Entities;
using AgentPlatform.Infrastructure.Repositories;

namespace AgentPlatform.Tests.Infrastructure;

/// <summary>
/// PART 4: tenant-изоляция через TenantScopedRepository&lt;T&gt;.
/// </summary>
public sealed class TenantScopedRepositoryTests
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Query_returns_only_records_of_current_tenant()
    {
        using var database = TestDatabase.Create();
        await SeedAgentsAsync(database);

        var repository = new TenantScopedRepository<Agent>(database.Context, FakeTenantContext.For(TenantA));

        var agents = await repository.ListAsync();

        Assert.Single(agents);
        Assert.Equal("agent-a", agents[0].Name);
        Assert.All(agents, agent => Assert.Equal(TenantA, agent.TenantId));
    }

    [Fact]
    public async Task Query_without_tenant_context_throws_InvalidOperationException()
    {
        using var database = TestDatabase.Create();
        var repository = new TenantScopedRepository<Agent>(database.Context, new FakeTenantContext());

        var exception = Assert.Throws<InvalidOperationException>(() => repository.Query.ToList());
        Assert.Contains("Tenant context is not resolved", exception.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ListAsync());
    }

    [Fact]
    public async Task AddAsync_without_tenant_context_throws_InvalidOperationException()
    {
        using var database = TestDatabase.Create();
        var repository = new TenantScopedRepository<Agent>(database.Context, new FakeTenantContext());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.AddAsync(new Agent { Name = "new-agent" }));
    }

    [Fact]
    public async Task AddAsync_stamps_tenant_id_from_context()
    {
        using var database = TestDatabase.Create();
        var repository = new TenantScopedRepository<Agent>(database.Context, FakeTenantContext.For(TenantA));

        var agent = await repository.AddAsync(new Agent { Name = "stamped-agent" });
        await repository.SaveChangesAsync();

        Assert.Equal(TenantA, agent.TenantId);
        Assert.Equal(TenantA, database.Context.Agents.Single(candidate => candidate.Name == "stamped-agent").TenantId);
    }

    [Fact]
    public async Task AddAsync_for_foreign_tenant_throws_InvalidOperationException()
    {
        using var database = TestDatabase.Create();
        var repository = new TenantScopedRepository<Agent>(database.Context, FakeTenantContext.For(TenantA));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.AddAsync(new Agent { Name = "foreign", TenantId = TenantB }));

        Assert.Contains("Cross-tenant insert", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindAsync_returns_null_for_other_tenant()
    {
        using var database = TestDatabase.Create();
        await SeedAgentsAsync(database);

        var foreignAgentId = database.Context.Agents.Single(agent => agent.Name == "agent-b").Id;
        var repository = new TenantScopedRepository<Agent>(database.Context, FakeTenantContext.For(TenantA));

        Assert.Null(await repository.FindAsync(foreignAgentId));
        Assert.NotNull(await repository.FindAsync(
            database.Context.Agents.Single(agent => agent.Name == "agent-a").Id));
    }

    [Fact]
    public async Task Update_of_foreign_tenant_throws_InvalidOperationException()
    {
        using var database = TestDatabase.Create();
        await SeedAgentsAsync(database);

        var foreignAgent = database.Context.Agents.Single(agent => agent.Name == "agent-b");
        var repository = new TenantScopedRepository<Agent>(database.Context, FakeTenantContext.For(TenantA));

        Assert.Throws<InvalidOperationException>(() => repository.Update(foreignAgent));
        Assert.Throws<InvalidOperationException>(() => repository.Remove(foreignAgent));
    }

    [Fact]
    public async Task Global_repository_reads_global_tables_without_tenant_filter()
    {
        using var database = TestDatabase.Create();
        database.Context.ConnectorRegistry.AddRange(
            new ConnectorRegistryEntry
            {
                Name = "echo",
                Version = "1.0.0",
                Manifest = "{}",
                ManifestHash = new string('a', 64),
                Publisher = "AgentPlatform"
            },
            new ConnectorRegistryEntry
            {
                Name = "crm",
                Version = "2.0.0",
                Manifest = "{}",
                ManifestHash = new string('b', 64),
                Publisher = "Vendor"
            });
        await database.Context.SaveChangesAsync();

        // Тенант-контекст задан, но глобальные таблицы фильтроваться не должны.
        var repository = new GlobalRepository<ConnectorRegistryEntry>(database.Context);

        var entries = await repository.ListAsync();

        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void Global_repository_rejects_tenant_scoped_entity()
    {
        using var database = TestDatabase.Create();

        var exception = Assert.Throws<InvalidOperationException>(() => new GlobalRepository<Agent>(database.Context));
        Assert.Contains("tenant-scoped", exception.Message, StringComparison.Ordinal);
    }

    private static async Task SeedAgentsAsync(TestDatabase database)
    {
        database.Context.Agents.AddRange(
            new Agent { TenantId = TenantA, Name = "agent-a" },
            new Agent { TenantId = TenantB, Name = "agent-b" });

        await database.Context.SaveChangesAsync();
    }
}
