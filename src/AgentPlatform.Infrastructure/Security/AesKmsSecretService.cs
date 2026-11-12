using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Json;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentPlatform.Infrastructure.Security;

/// <summary>
/// Secret Service v1: AES-256-GCM, ключ шифрования приходит из KMS-заглушки.
/// <para>
/// Контейнер: <c>base64(nonce[12] | tag[16] | ciphertext)</c>. В БД (tenant_secrets)
/// попадает только контейнер, в connector_installations — только ссылка на него.
/// </para>
/// <para>
/// KMS-заглушка: мастер-ключ читается из env <see cref="MasterKeyEnvironmentVariable"/>.
/// TODO(kms): заменить на реальный KMS (Vault/KMS провайдера), ключи версионировать;
/// контракт <see cref="ISecretService"/> при этом не меняется.
/// </para>
/// </summary>
public sealed class AesKmsSecretService(
    AgentPlatformDbContext context,
    ILogger<AesKmsSecretService> logger) : ISecretService
{
    /// <summary>Переменная окружения с base64-ключом AES-256 (32 байта).</summary>
    public const string MasterKeyEnvironmentVariable = "AGENTPLATFORM_SECRETS_MASTER_KEY";

    /// <summary>Префикс ссылок Secret Service.</summary>
    public const string RefPrefix = "secret://local/";

    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <summary>
    /// Dev-ключ KMS-заглушки, если env не задан. В production сервис без env
    /// продолжает работать, но пишет предупреждение (TODO(kms)).
    /// </summary>
    private static readonly byte[] DevelopmentFallbackKey =
        Encoding.ASCII.GetBytes("agentplatform-dev-master-key-32b");

    private readonly AgentPlatformDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    private readonly ILogger<AesKmsSecretService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<string> StoreAsync(
        Guid tenantId,
        IReadOnlyDictionary<string, string> credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        RequireTenant(tenantId);

        var reference = RefPrefix + Guid.NewGuid().ToString("N");
        var entity = new TenantSecret
        {
            TenantId = tenantId,
            Ref = reference,
            CipherText = Encrypt(credentials)
        };

        _context.TenantSecrets.Add(entity);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return reference;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>?> GetAsync(
        Guid tenantId,
        string credentialsRef,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);

        if (string.IsNullOrWhiteSpace(credentialsRef))
        {
            return null;
        }

        var entity = await _context.TenantSecrets
            .AsNoTracking()
            .FirstOrDefaultAsync(
                secret => secret.TenantId == tenantId && secret.Ref == credentialsRef,
                cancellationToken)
            .ConfigureAwait(false);

        return entity is null ? null : Decrypt(entity.CipherText);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        Guid tenantId,
        string credentialsRef,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);

        if (string.IsNullOrWhiteSpace(credentialsRef))
        {
            return false;
        }

        var entity = await _context.TenantSecrets
            .FirstOrDefaultAsync(
                secret => secret.TenantId == tenantId && secret.Ref == credentialsRef,
                cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            return false;
        }

        _context.TenantSecrets.Remove(entity);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    private string Encrypt(IReadOnlyDictionary<string, string> credentials)
    {
        var json = JsonSerializer.Serialize(credentials, PlatformJson.DbOptions);
        var plaintext = Encoding.UTF8.GetBytes(json);
        var key = ResolveMasterKey();

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var container = new byte[NonceSize + TagSize + ciphertext.Length];
        nonce.CopyTo(container, 0);
        tag.CopyTo(container, NonceSize);
        ciphertext.CopyTo(container, NonceSize + TagSize);

        return Convert.ToBase64String(container);
    }

    private IReadOnlyDictionary<string, string> Decrypt(string container)
    {
        var payload = Convert.FromBase64String(container);

        if (payload.Length <= NonceSize + TagSize)
        {
            throw new InvalidOperationException("tenant secret container is corrupted");
        }

        var nonce = payload.AsSpan(0, NonceSize);
        var tag = payload.AsSpan(NonceSize, TagSize);
        var ciphertext = payload.AsSpan(NonceSize + TagSize);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(ResolveMasterKey(), TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext, PlatformJson.DbOptions)
            ?? throw new InvalidOperationException("tenant secret payload is empty");
    }

    private byte[] ResolveMasterKey()
    {
        var configured = Environment.GetEnvironmentVariable(MasterKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            // TODO(kms): реальный KMS обязателен в production.
            _logger.LogWarning(
                "KMS stub: {Variable} is not set, the development master key is used. " +
                "Configure a real KMS before production use.",
                MasterKeyEnvironmentVariable);

            return DevelopmentFallbackKey;
        }

        var key = Convert.FromBase64String(configured);
        if (key.Length != 32)
        {
            throw new InvalidOperationException(
                $"{MasterKeyEnvironmentVariable} must be a base64-encoded 32-byte AES-256 key.");
        }

        return key;
    }

    private static void RequireTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("tenant_id is required", nameof(tenantId));
        }
    }
}
