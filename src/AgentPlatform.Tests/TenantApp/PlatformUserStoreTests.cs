using AgentPlatform.Infrastructure.Persistence.Entities;
using AgentPlatform.Infrastructure.Seeding;
using AgentPlatform.TenantApp.Web;
using AgentPlatform.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;

namespace AgentPlatform.Tests.TenantApp;

/// <summary>
/// ASP.NET Identity store над users: поиск по email без учёта регистра,
/// совместимость PBKDF2-хеша seed и кабинета, роли из users.role.
/// </summary>
public sealed class PlatformUserStoreTests
{
    [Fact]
    public async Task Finds_user_by_normalized_email_and_verifies_seed_password()
    {
        using var database = TestDatabase.Create();
        var passwordHasher = new Pbkdf2PasswordHasher();

        var user = new User
        {
            TenantId = Guid.NewGuid(),
            Email = "admin@demo.local",
            PasswordHash = passwordHasher.Hash("S3cure-Passw0rd!"),
            Role = "admin",
            AuthMeta = "{}"
        };

        database.Context.Users.Add(user);
        await database.Context.SaveChangesAsync();

        var store = new PlatformUserStore(database.Context);
        var found = await store.FindByEmailAsync("ADMIN@DEMO.LOCAL", CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(user.Id, found!.Id);

        var identityHasher = new PlatformIdentityPasswordHasher(passwordHasher);
        Assert.Equal(
            PasswordVerificationResult.Success,
            identityHasher.VerifyHashedPassword(found, found.PasswordHash, "S3cure-Passw0rd!"));
        Assert.Equal(
            PasswordVerificationResult.Failed,
            identityHasher.VerifyHashedPassword(found, found.PasswordHash, "wrong-password"));
    }

    [Fact]
    public async Task Unknown_or_malformed_user_id_returns_null()
    {
        using var database = TestDatabase.Create();
        var store = new PlatformUserStore(database.Context);

        Assert.Null(await store.FindByIdAsync("not-a-guid", CancellationToken.None));
        Assert.Null(await store.FindByIdAsync(Guid.NewGuid().ToString("D"), CancellationToken.None));
        Assert.Null(await store.FindByNameAsync(string.Empty, CancellationToken.None));
    }
}
