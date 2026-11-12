using AgentPlatform.Infrastructure.Configuration;
using AgentPlatform.Infrastructure.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPlatform.Tests.Infrastructure;

/// <summary>Проверочный seed: тенант + admin, пароль только из env, идемпотентность.</summary>
public sealed class DatabaseSeederTests
{
    private const string AdminPassword = "S3cure-Passw0rd!";

    [Fact]
    public async Task Seed_creates_tenant_and_admin_with_hashed_password()
    {
        using var database = TestDatabase.Create();
        var hasher = new Pbkdf2PasswordHasher();
        var seeder = new DatabaseSeeder(database.Context, hasher, CreateOptions(), NullLogger<DatabaseSeeder>.Instance);

        var result = await seeder.SeedAsync();

        Assert.True(result.TenantCreated);
        Assert.True(result.UserCreated);

        var tenant = await database.Context.Tenants.SingleAsync();
        Assert.Equal("Demo Tenant", tenant.Name);
        Assert.Equal("pilot", tenant.Plan);
        Assert.Equal("active", tenant.Status);
        Assert.Contains("\"mode\":\"platform\"", tenant.LlmConfig, StringComparison.Ordinal);

        var admin = await database.Context.Users.SingleAsync();
        Assert.Equal(result.UserId, admin.Id);
        Assert.Equal(result.TenantId, admin.TenantId);
        Assert.Equal("admin@demo.local", admin.Email);
        Assert.Equal("admin", admin.Role);
        Assert.DoesNotContain(AdminPassword, admin.PasswordHash, StringComparison.Ordinal);
        Assert.True(hasher.Verify(AdminPassword, admin.PasswordHash));
    }

    [Fact]
    public async Task Seed_is_idempotent()
    {
        using var database = TestDatabase.Create();
        var hasher = new Pbkdf2PasswordHasher();
        var seeder = new DatabaseSeeder(database.Context, hasher, CreateOptions(), NullLogger<DatabaseSeeder>.Instance);

        await seeder.SeedAsync();
        var secondRun = await seeder.SeedAsync();

        Assert.False(secondRun.TenantCreated);
        Assert.False(secondRun.UserCreated);
        Assert.Equal(1, await database.Context.Tenants.CountAsync());
        Assert.Equal(1, await database.Context.Users.CountAsync());
    }

    [Fact]
    public async Task Seed_without_admin_password_throws()
    {
        using var database = TestDatabase.Create();
        var options = CreateOptions();
        options.AdminPassword = null;
        var seeder = new DatabaseSeeder(
            database.Context,
            new Pbkdf2PasswordHasher(),
            options,
            NullLogger<DatabaseSeeder>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.SeedAsync());
        Assert.Contains(SeedOptions.PasswordEnvironmentVariable, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pbkdf2_hasher_rejects_foreign_hash_formats()
    {
        var hasher = new Pbkdf2PasswordHasher();

        Assert.False(hasher.Verify(AdminPassword, "not-a-hash"));
        Assert.False(hasher.Verify(AdminPassword, "PBKDF2-SHA256$0$AAAA$BBBB"));
    }

    private static SeedOptions CreateOptions() => new()
    {
        TenantName = "Demo Tenant",
        TenantPlan = "pilot",
        AdminEmail = " Admin@Demo.Local ",
        AdminPassword = AdminPassword
    };
}
