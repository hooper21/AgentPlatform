using AgentPlatform.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentPlatform.Infrastructure.Persistence.Configurations;

/// <summary>usage — суточная агрегация использования.</summary>
public sealed class UsageRecordConfiguration : IEntityTypeConfiguration<UsageRecord>
{
    public void Configure(EntityTypeBuilder<UsageRecord> builder)
    {
        builder.ToTable("usage");
        builder.HasKey(record => record.Id);
        builder.Property(record => record.Id).UseIdentityByDefaultColumn();

        builder.Property(record => record.Period).HasColumnType("date").IsRequired();
        builder.Property(record => record.Dialogs).IsRequired().HasDefaultValue(0);
        builder.Property(record => record.ToolCalls).IsRequired().HasDefaultValue(0);
        builder.Property(record => record.TokensIn).IsRequired().HasDefaultValue(0L);
        builder.Property(record => record.TokensOut).IsRequired().HasDefaultValue(0L);

        // Уникальность (tenant_id, period, agent_id) — tenant-first индекс таблицы.
        builder.HasIndex(record => new { record.TenantId, record.Period, record.AgentId })
            .IsUnique()
            .HasDatabaseName("ux_usage_tenant_period_agent");

        builder.HasOne(record => record.Agent)
            .WithMany()
            .HasForeignKey(record => record.AgentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(record => record.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>scheduled_runs — расписание запусков агентов планировщиком.</summary>
public sealed class ScheduledRunConfiguration : IEntityTypeConfiguration<ScheduledRun>
{
    public void Configure(EntityTypeBuilder<ScheduledRun> builder)
    {
        builder.ToTable("scheduled_runs");
        builder.HasKey(run => run.Id);

        builder.Property(run => run.CronExpression).HasMaxLength(100).IsRequired();
        builder.Property(run => run.Enabled).IsRequired().HasDefaultValue(true);
        builder.Property(run => run.LastRunAt).IsNullableTimestamp();
        builder.Property(run => run.LastRunStatus).HasMaxLength(20);
        builder.Property(run => run.CreatedAt).IsTimestamp();
        builder.Property(run => run.UpdatedAt).IsTimestamp();

        builder.HasIndex(run => run.Enabled).HasDatabaseName("ix_sr_enabled");

        // Добавлено сверх DDL: tenant-first индекс (в DDL только ix_sr_enabled).
        builder.HasIndex(run => new { run.TenantId, run.Enabled }).HasDatabaseName("ix_sr_tenant");

        builder.HasOne(run => run.Agent)
            .WithMany()
            .HasForeignKey(run => run.AgentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(run => run.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
