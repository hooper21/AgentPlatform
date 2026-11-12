using AgentPlatform.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentPlatform.Infrastructure.Persistence.Configurations;

/// <summary>tenant_secrets — шифрованные секреты тенантов (Secret Service).</summary>
public sealed class TenantSecretConfiguration : IEntityTypeConfiguration<TenantSecret>
{
    public void Configure(EntityTypeBuilder<TenantSecret> builder)
    {
        builder.ToTable("tenant_secrets");
        builder.HasKey(secret => secret.Id);

        builder.Property(secret => secret.Ref).HasMaxLength(200).IsRequired();
        builder.Property(secret => secret.CipherText).HasColumnType("text").IsRequired();
        builder.Property(secret => secret.CreatedAt).IsTimestamp();
        builder.Property(secret => secret.UpdatedAt).IsTimestamp();

        // Ссылка уникальна внутри тенанта: секрет одного тенанта не виден другому.
        builder.HasIndex(secret => new { secret.TenantId, secret.Ref })
            .IsUnique()
            .HasDatabaseName("ux_tenant_secrets_tenant_ref");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(secret => secret.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
