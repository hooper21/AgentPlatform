using AgentPlatform.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentPlatform.Infrastructure.Persistence.Configurations;

/// <summary>sessions — сессии диалогов.</summary>
public sealed class AgentSessionConfiguration : IEntityTypeConfiguration<AgentSession>
{
    public void Configure(EntityTypeBuilder<AgentSession> builder)
    {
        builder.ToTable("sessions");
        builder.HasKey(session => session.Id);

        builder.Property(session => session.UserRef).HasMaxLength(200).IsRequired();
        builder.Property(session => session.Status).HasMaxLength(20).IsRequired().HasDefaultValue("active");
        builder.Property(session => session.Meta).IsJsonb();
        builder.Property(session => session.StartedAt).IsTimestamp();
        builder.Property(session => session.LastActivityAt).IsTimestamp();
        builder.Property(session => session.ClosedAt).IsNullableTimestamp();

        builder.HasIndex(session => new { session.TenantId, session.Status }).HasDatabaseName("ix_se_tenant_status");
        builder.HasIndex(session => new { session.TenantId, session.UserRef }).HasDatabaseName("ix_se_user");

        builder.HasOne(session => session.Agent)
            .WithMany()
            .HasForeignKey(session => session.AgentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(session => session.Channel)
            .WithMany()
            .HasForeignKey(session => session.ChannelId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(session => session.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>messages — сообщения диалога (каскадное удаление вместе с сессией).</summary>
public sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("messages");
        builder.HasKey(message => message.Id);
        builder.Property(message => message.Id).UseIdentityByDefaultColumn();

        builder.Property(message => message.Role).HasMaxLength(20).IsRequired();
        builder.Property(message => message.Content).HasColumnType("text").IsRequired();
        builder.Property(message => message.ToolCalls).HasColumnType("jsonb");
        builder.Property(message => message.Ts).IsTimestamp();

        builder.HasIndex(message => new { message.SessionId, message.Ts }).HasDatabaseName("ix_me_session");

        // Добавлено сверх DDL: tenant-first индекс для tenant-фильтров по сообщениям.
        builder.HasIndex(message => new { message.TenantId, message.Ts }).HasDatabaseName("ix_me_tenant_ts");

        builder.HasOne(message => message.Session)
            .WithMany(session => session.Messages)
            .HasForeignKey(message => message.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(message => message.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>checkpoints — чекпоинты сессий (каскадное удаление вместе с сессией).</summary>
public sealed class CheckpointConfiguration : IEntityTypeConfiguration<Checkpoint>
{
    public void Configure(EntityTypeBuilder<Checkpoint> builder)
    {
        builder.ToTable("checkpoints");
        builder.HasKey(checkpoint => checkpoint.Id);
        builder.Property(checkpoint => checkpoint.Id).UseIdentityByDefaultColumn();

        builder.Property(checkpoint => checkpoint.State).IsJsonb(defaultSql: null);
        builder.Property(checkpoint => checkpoint.GraphSignatureHash).HasMaxLength(64).IsRequired();
        builder.Property(checkpoint => checkpoint.CreatedAt).IsTimestamp();

        // UNIQUE (session_id, superstep). В DDL рядом объявлен дублирующий
        // ix_cp_session (session_id, superstep DESC) — оставлен один уникальный индекс,
        // он же обслуживает выборку последнего чекпоинта, см. README.
        builder.HasIndex(checkpoint => new { checkpoint.SessionId, checkpoint.Superstep })
            .IsUnique()
            .HasDatabaseName("ux_checkpoints_session_superstep");

        // Добавлено сверх DDL: tenant-first индекс для tenant-фильтров по чекпоинтам.
        builder.HasIndex(checkpoint => new { checkpoint.TenantId, checkpoint.SessionId }).HasDatabaseName("ix_cp_tenant");

        builder.HasOne(checkpoint => checkpoint.Session)
            .WithMany(session => session.Checkpoints)
            .HasForeignKey(checkpoint => checkpoint.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(checkpoint => checkpoint.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
