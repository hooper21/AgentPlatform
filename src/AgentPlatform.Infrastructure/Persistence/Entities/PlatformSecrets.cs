using AgentPlatform.Core.Abstractions;

namespace AgentPlatform.Infrastructure.Persistence.Entities;

/// <summary>
/// Шифрованный секрет тенанта (таблица tenant_secrets) — хранилище Secret Service.
/// В строке лежит только AES-256-GCM контейнер; ключ шифрования — вне БД (KMS-заглушка).
/// <para>
/// Ссылка (<see cref="Ref"/>) сохраняется в connector_installations.credentials_ref.
/// </para>
/// </summary>
public sealed class TenantSecret : ITenantEntity, IHasCreatedAt, IHasUpdatedAt
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    /// <summary>Ссылка на секрет: secret://local/{id}.</summary>
    public string Ref { get; set; } = string.Empty;

    /// <summary>AES-256-GCM контейнер: base64(nonce | tag | ciphertext).</summary>
    public string CipherText { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
