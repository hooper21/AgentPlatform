using AgentPlatform.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentPlatform.Infrastructure.Persistence.Configurations;

/// <summary>channels — каналы тенанта (widget | telegram | плагины).</summary>
public sealed class ChannelConfiguration : IEntityTypeConfiguration<Channel>
{
    public void Configure(EntityTypeBuilder<Channel> builder)
    {
        builder.ToTable("channels");
        builder.HasKey(channel => channel.Id);

        builder.Property(channel => channel.Type).HasMaxLength(30).IsRequired();
        builder.Property(channel => channel.Config).IsJsonb(defaultSql: null);
        builder.Property(channel => channel.WidgetSecretRef).HasMaxLength(200);
        builder.Property(channel => channel.Status).HasMaxLength(20).IsRequired().HasDefaultValue("active");
        builder.Property(channel => channel.CreatedAt).IsTimestamp();
        builder.Property(channel => channel.UpdatedAt).IsTimestamp();

        builder.HasIndex(channel => channel.TenantId).HasDatabaseName("ix_ch_tenant");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(channel => channel.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>knowledge_sources — источники знаний тенанта.</summary>
public sealed class KnowledgeSourceConfiguration : IEntityTypeConfiguration<KnowledgeSource>
{
    public void Configure(EntityTypeBuilder<KnowledgeSource> builder)
    {
        builder.ToTable("knowledge_sources");
        builder.HasKey(source => source.Id);

        builder.Property(source => source.Type).HasMaxLength(30).IsRequired();
        builder.Property(source => source.Ref).HasMaxLength(500).IsRequired();
        builder.Property(source => source.Title).HasMaxLength(300).IsRequired();
        builder.Property(source => source.ChunkCount).IsRequired().HasDefaultValue(0);
        builder.Property(source => source.IndexStatus).HasMaxLength(20).IsRequired().HasDefaultValue("pending");
        builder.Property(source => source.IndexMeta).IsJsonb();
        builder.Property(source => source.CreatedAt).IsTimestamp();
        builder.Property(source => source.UpdatedAt).IsTimestamp();

        builder.HasIndex(source => source.TenantId).HasDatabaseName("ix_ks_tenant");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(source => source.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// knowledge_chunks — чанки источников (bigserial PK).
/// Столбец embedding vector(1024) и ivfflat-индекс создаются в миграции
/// сырым SQL (pgvector), в EF-модель не входят: провайдер pgvector не подключён.
/// </summary>
public sealed class KnowledgeChunkConfiguration : IEntityTypeConfiguration<KnowledgeChunk>
{
    public void Configure(EntityTypeBuilder<KnowledgeChunk> builder)
    {
        builder.ToTable("knowledge_chunks");
        builder.HasKey(chunk => chunk.Id);
        builder.Property(chunk => chunk.Id).UseIdentityByDefaultColumn();

        builder.Property(chunk => chunk.Content).HasColumnType("text").IsRequired();
        builder.Property(chunk => chunk.Metadata).IsJsonb();
        builder.Property(chunk => chunk.CreatedAt).IsTimestamp();

        builder.HasIndex(chunk => new { chunk.TenantId, chunk.SourceId }).HasDatabaseName("ix_kc_tenant_source");

        builder.HasOne(chunk => chunk.Source)
            .WithMany()
            .HasForeignKey(chunk => chunk.SourceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(chunk => chunk.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
