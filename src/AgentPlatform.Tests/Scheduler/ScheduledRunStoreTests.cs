using AgentPlatform.Infrastructure.Repositories;
using AgentPlatform.Tests.Infrastructure;

namespace AgentPlatform.Tests.Scheduler;

/// <summary>Расписания планировщика: tenant-изоляция CRUD-операций.</summary>
public sealed class ScheduledRunStoreTests
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AgentA = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid AgentB = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public async Task List_and_find_are_tenant_scoped()
    {
        using var database = TestDatabase.Create();
        var store = new EfScheduledRunStore(database.Context);

        var runA = await store.CreateAsync(TenantA, AgentA, "* * * * *", enabled: true);
        var runB = await store.CreateAsync(TenantB, AgentB, "0 * * * *", enabled: true);

        var listA = await store.ListAsync(TenantA);
        Assert.Single(listA);
        Assert.Equal(runA.Id, listA[0].Id);

        Assert.Null(await store.FindAsync(TenantA, runB.Id));
        Assert.NotNull(await store.FindAsync(TenantB, runB.Id));
    }

    [Fact]
    public async Task Update_and_delete_do_not_touch_other_tenant()
    {
        using var database = TestDatabase.Create();
        var store = new EfScheduledRunStore(database.Context);

        var runA = await store.CreateAsync(TenantA, AgentA, "* * * * *", enabled: true);
        var runB = await store.CreateAsync(TenantB, AgentB, "0 * * * *", enabled: true);

        Assert.Null(await store.UpdateAsync(TenantA, runB.Id, AgentA, "*/5 * * * *", enabled: false));
        Assert.False(await store.DeleteAsync(TenantA, runB.Id));

        var untouched = await store.FindAsync(TenantB, runB.Id);
        Assert.NotNull(untouched);
        Assert.Equal("0 * * * *", untouched!.CronExpression);
        Assert.True(untouched.Enabled);

        var updated = await store.UpdateAsync(TenantA, runA.Id, AgentA, "*/5 * * * *", enabled: false);
        Assert.NotNull(updated);
        Assert.Equal("*/5 * * * *", updated!.CronExpression);
        Assert.False(updated.Enabled);

        Assert.True(await store.DeleteAsync(TenantA, runA.Id));
        Assert.Empty(await store.ListAsync(TenantA));
    }
}
