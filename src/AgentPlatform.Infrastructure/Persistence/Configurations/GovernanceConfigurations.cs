using AgentPlatform.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentPlatform.Infrastructure.Persistence.Configurations;

/// <summary>approvals — журнал HITL-заявок.</summary>
public sealed class ApprovalConfiguration : IEntityTypeConfiguration<Approval>
{
    public void Configure(EntityTypeBuilder<Approval> builder)
    {
        builder.ToTable("approvals");
        builder.HasKey(approval => approval.Id);

        // В DDL тип столбца — bigserial, но это nullable-ссылка на чекпоинт:
        // sequence на FK дала бы мусорные значения, поэтому столбец bigint NULL.
        builder.Property(approval => approval.CheckpointId).HasColumnType("bigint");
        builder.Property(approval => approval.GatePayload).IsJsonb(defaultSql: null);
        builder.Property(approval => approval.Status).HasMaxLength(20).IsRequired().HasDefaultValue("pending");
        builder.Property(approval => approval.RequestedAt).IsTimestamp();
        builder.Property(approval => approval.ExpiresAt).IsNullableTimestamp();
        builder.Property(approval => approval.DecidedByKind).HasMaxLength(20);
        builder.Property(approval => approval.Decision).HasMaxLength(20);
        builder.Property(approval => approval.DecidedAt).IsNullableTimestamp();

        builder.HasIndex(approval => new { approval.TenantId, approval.Status })
            .HasDatabaseName("ix_ap_pending")
            .HasFilter("\"status\" = 'pending'");

        builder.HasOne(approval => approval.Session)
            .WithMany()
            .HasForeignKey(approval => approval.SessionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(approval => approval.Checkpoint)
            .WithMany()
            .HasForeignKey(approval => approval.CheckpointId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(approval => approval.DecisionAuthor)
            .WithMany()
            .HasForeignKey(approval => approval.DecidedBy)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(approval => approval.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// tool_audit — append-only аудит вызовов тулов.
/// UPDATE/DELETE блокируются кодом в AgentPlatformDbContext.
/// </summary>
public sealed class ToolAuditEntryConfiguration : IEntityTypeConfiguration<ToolAuditEntry>
{
    public void Configure(EntityTypeBuilder<ToolAuditEntry> builder)
    {
        builder.ToTable("tool_audit");
        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Id).UseIdentityByDefaultColumn();

        builder.Property(entry => entry.ToolName).HasMaxLength(200).IsRequired();
        builder.Property(entry => entry.ArgsMasked).IsJsonb(defaultSql: null);
        builder.Property(entry => entry.Outcome).HasMaxLength(20).IsRequired();
        builder.Property(entry => entry.ResultRef).HasMaxLength(500);
        builder.Property(entry => entry.TraceId).HasMaxLength(64).IsRequired();
        builder.Property(entry => entry.Ts).IsTimestamp();

        builder.HasIndex(entry => new { entry.TenantId, entry.Ts })
            .IsDescending(false, true)
            .HasDatabaseName("ix_ta_tenant_ts");
        builder.HasIndex(entry => new { entry.TenantId, entry.ToolName, entry.Ts })
            .IsDescending(false, false, true)
            .HasDatabaseName("ix_ta_tool");

        builder.HasOne(entry => entry.Session)
            .WithMany()
            .HasForeignKey(entry => entry.SessionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(entry => entry.Installation)
            .WithMany()
            .HasForeignKey(entry => entry.InstallationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(entry => entry.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
