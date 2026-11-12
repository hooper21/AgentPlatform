using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Json;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Persistence.Interceptors;
using AgentPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AgentPlatform.Tests.Runtime;

/// <summary>
/// EfCheckpointStore: чекпоинты хранят нейтральный JSON состояния, последний
/// чекпоинт определяется по суперстепу, tenant-изоляция и валидация при записи.
/// </summary>
public sealed class EfCheckpointStoreTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SessionId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    [Fact]
    public async Task Latest_checkpoint_is_returned_by_superstep()
    {
        using var context = CreateContext();
        var store = new EfCheckpointStore(context);

        await store.SaveAsync(Checkpoint(superstep: 1, "первый"));
        await store.SaveAsync(Checkpoint(superstep: 2, "второй"));

        var latest = await store.GetLatestAsync(TenantId, SessionId);

        Assert.NotNull(latest);
        Assert.Equal(2, latest!.Superstep);
        Assert.Equal("test.graph.v1", latest.GraphSignatureHash);

        var state = StateContract.DeserializeRequired(latest.State);
        Assert.Equal("второй", state.Messages[^1].Content);
    }

    [Fact]
    public async Task Checkpoint_with_foreign_tenant_or_session_is_not_returned()
    {
        using var context = CreateContext();
        var store = new EfCheckpointStore(context);

        await store.SaveAsync(Checkpoint(superstep: 1, "первый"));

        Assert.Null(await store.GetLatestAsync(Guid.NewGuid(), SessionId));
        Assert.Null(await store.GetLatestAsync(TenantId, Guid.NewGuid()));
    }

    [Fact]
    public async Task State_that_violates_contract_is_not_written()
    {
        using var context = CreateContext();
        var store = new EfCheckpointStore(context);

        var invalid = new SessionCheckpoint(
            TenantId,
            SessionId,
            1,
            """{"version":1,"messages":[],"counters":{},"maf_checkpoint":{"superstep":1}}""",
            "test.graph.v1",
            DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<JsonValidationException>(() => store.SaveAsync(invalid));
        Assert.Null(await store.GetLatestAsync(TenantId, SessionId));
    }

    private static SessionCheckpoint Checkpoint(int superstep, string content) => new(
        TenantId,
        SessionId,
        superstep,
        StateContract.Serialize(new SessionState
        {
            Messages = [new StateMessage(MessageRole.User, content, DateTimeOffset.UtcNow)]
        }),
        "test.graph.v1",
        DateTimeOffset.UtcNow);

    private static AgentPlatformDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AgentPlatformDbContext>()
            .UseInMemoryDatabase($"agentplatform-checkpoints-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(new AuditingInterceptor())
            .Options;

        var context = new AgentPlatformDbContext(options);
        context.Database.EnsureCreated();

        return context;
    }
}
