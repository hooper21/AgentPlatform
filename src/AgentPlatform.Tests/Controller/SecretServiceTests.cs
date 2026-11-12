using System.Security.Cryptography;
using AgentPlatform.Infrastructure.Persistence.Entities;
using AgentPlatform.Infrastructure.Security;
using AgentPlatform.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPlatform.Tests.Controller;

/// <summary>
/// Secret Service: AES-256-GCM контейнер в tenant_secrets, изоляция по тенанту,
/// обнаружение подмены (GCM tag) и удаление по ссылке.
/// </summary>
public sealed class SecretServiceTests
{
    [Fact]
    public async Task Store_get_roundtrip_and_tenant_isolation()
    {
        using var database = TestDatabase.Create();
        var service = new AesKmsSecretService(database.Context, NullLogger<AesKmsSecretService>.Instance);

        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        database.Context.Tenants.AddRange(
            new Tenant { Id = tenantId, Name = "tenant-a" },
            new Tenant { Id = otherTenantId, Name = "tenant-b" });
        await database.Context.SaveChangesAsync();

        var reference = await service.StoreAsync(
            tenantId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["api_key"] = "super-secret-value",
                ["region"] = "ru"
            });

        Assert.StartsWith(AesKmsSecretService.RefPrefix, reference, StringComparison.Ordinal);

        var stored = await database.Context.TenantSecrets.SingleAsync();
        Assert.Equal(tenantId, stored.TenantId);
        Assert.Equal(reference, stored.Ref);
        Assert.DoesNotContain("super-secret-value", stored.CipherText, StringComparison.Ordinal);

        var loaded = await service.GetAsync(tenantId, reference);
        Assert.NotNull(loaded);
        Assert.Equal("super-secret-value", loaded!["api_key"]);
        Assert.Equal("ru", loaded["region"]);

        // Другой тенант ссылку не видит и удалить не может.
        Assert.Null(await service.GetAsync(otherTenantId, reference));
        Assert.False(await service.DeleteAsync(otherTenantId, reference));
        Assert.Equal(1, await database.Context.TenantSecrets.CountAsync());

        Assert.True(await service.DeleteAsync(tenantId, reference));
        Assert.Equal(0, await database.Context.TenantSecrets.CountAsync());
    }

    [Fact]
    public async Task Tampered_ciphertext_is_rejected_by_aes_gcm()
    {
        using var database = TestDatabase.Create();
        var service = new AesKmsSecretService(database.Context, NullLogger<AesKmsSecretService>.Instance);

        var tenantId = Guid.NewGuid();
        var reference = await service.StoreAsync(
            tenantId,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "value" });

        var stored = await database.Context.TenantSecrets.SingleAsync();
        var payload = Convert.FromBase64String(stored.CipherText);
        payload[^1] ^= 0xFF;
        stored.CipherText = Convert.ToBase64String(payload);
        await database.Context.SaveChangesAsync();

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => service.GetAsync(tenantId, reference));
    }

    [Fact]
    public async Task Empty_tenant_id_is_rejected()
    {
        using var database = TestDatabase.Create();
        var service = new AesKmsSecretService(database.Context, NullLogger<AesKmsSecretService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => service.StoreAsync(
            Guid.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["a"] = "b" }));
    }
}
