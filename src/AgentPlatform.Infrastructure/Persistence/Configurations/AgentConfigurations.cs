using AgentPlatform.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentPlatform.Infrastructure.Persistence.Configurations;

/// <summary>agents — агенты тенанта.</summary>
public sealed class AgentConfiguration : IEntityTypeConfiguration<Agent>
{
    public void Configure(EntityTypeBuilder<Agent> builder)
    {
        builder.ToTable("agents");
        builder.HasKey(agent => agent.Id);

        builder.Property(agent => agent.Name).HasMaxLength(200).IsRequired();
        builder.Property(agent => agent.CurrentVersion).IsRequired().HasDefaultValue(1);
        builder.Property(agent => agent.CreatedAt).IsTimestamp();
        builder.Property(agent => agent.UpdatedAt).IsTimestamp();

        // Уникальность (tenant_id, name) одновременно даёт tenant-first B-tree индекс.
        builder.HasIndex(agent => new { agent.TenantId, agent.Name }).IsUnique().HasDatabaseName("ux_agents_tenant_name");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(agent => agent.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>agent_versions — неизменяемые версии агента.</summary>
public sealed class AgentVersionConfiguration : IEntityTypeConfiguration<AgentVersion>
{
    public void Configure(EntityTypeBuilder<AgentVersion> builder)
    {
        builder.ToTable("agent_versions");
        builder.HasKey(version => version.Id);

        builder.Property(version => version.Status).HasMaxLength(20).IsRequired();
        builder.Property(version => version.SystemPrompt).HasColumnType("text").IsRequired();
        builder.Property(version => version.ModelRef).IsJsonb(defaultSql: null);
        builder.Property(version => version.EnabledTools).IsJsonb(DbDefaults.JsonArray);
        builder.Property(version => version.Rules).IsJsonb();
        builder.Property(version => version.EscalationRules).IsJsonb();
        builder.Property(version => version.Limits).IsJsonb();
        builder.Property(version => version.ProposedByOps).IsRequired().HasDefaultValue(false);
        builder.Property(version => version.CreatedAt).IsTimestamp();

        // UNIQUE (agent_id, version). В DDL рядом объявлен дублирующий ix_av_agent
        // на тех же столбцах — оставлен один уникальный индекс (правая часть DDL-требования
        // «индекс по (agent_id, version)» выполняется им же), см. README.
        builder.HasIndex(version => new { version.AgentId, version.Version })
            .IsUnique()
            .HasDatabaseName("ux_agent_versions_agent_version");

        builder.HasOne(version => version.Agent)
            .WithMany(agent => agent.Versions)
            .HasForeignKey(version => version.AgentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(version => version.Author)
            .WithMany()
            .HasForeignKey(version => version.CreatedBy)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
