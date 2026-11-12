using AgentPlatform.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentPlatform.Infrastructure.Persistence.Configurations;

/// <summary>connector_installations — установки коннекторов в тенантах.</summary>
public sealed class ConnectorInstallationConfiguration : IEntityTypeConfiguration<ConnectorInstallation>
{
    public void Configure(EntityTypeBuilder<ConnectorInstallation> builder)
    {
        builder.ToTable("connector_installations");
        builder.HasKey(installation => installation.Id);

        builder.Property(installation => installation.CredentialsRef).HasMaxLength(200).IsRequired();
        builder.Property(installation => installation.AuthType).HasMaxLength(30).IsRequired();
        builder.Property(installation => installation.ExternalUrl).HasMaxLength(500);
        builder.Property(installation => installation.Status).HasMaxLength(20).IsRequired().HasDefaultValue("active");
        builder.Property(installation => installation.LastHealthCheck).IsNullableTimestamp();
        builder.Property(installation => installation.HealthMeta).IsJsonb();
        builder.Property(installation => installation.CreatedAt).IsTimestamp();
        builder.Property(installation => installation.UpdatedAt).IsTimestamp();

        builder.HasIndex(installation => installation.TenantId).HasDatabaseName("ix_ci_tenant");

        builder.HasOne(installation => installation.Registry)
            .WithMany()
            .HasForeignKey(installation => installation.RegistryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(installation => installation.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// tool_settings — настройки тулов установки. В DDL у таблицы нет tenant_id,
/// поэтому tenant-изоляция выполняется через connector_installations (join);
/// tenant-фильтрованный доступ к тулам появится вместе с Policy Engine (задача 2.x).
/// </summary>
public sealed class ToolSettingConfiguration : IEntityTypeConfiguration<ToolSetting>
{
    public void Configure(EntityTypeBuilder<ToolSetting> builder)
    {
        builder.ToTable("tool_settings");
        builder.HasKey(setting => setting.Id);

        builder.Property(setting => setting.ToolName).HasMaxLength(200).IsRequired();
        builder.Property(setting => setting.Enabled).IsRequired().HasDefaultValue(true);
        builder.Property(setting => setting.Policy).HasMaxLength(20).IsRequired();
        builder.Property(setting => setting.PolicySource).HasMaxLength(20).IsRequired();
        builder.Property(setting => setting.DescriptionOverride).HasColumnType("text");
        builder.Property(setting => setting.GateSettings).IsJsonb();
        builder.Property(setting => setting.CreatedAt).IsTimestamp();
        builder.Property(setting => setting.UpdatedAt).IsTimestamp();

        builder.HasIndex(setting => new { setting.InstallationId, setting.ToolName })
            .IsUnique()
            .HasDatabaseName("ux_tool_settings_installation_tool");
        builder.HasIndex(setting => setting.InstallationId).HasDatabaseName("ix_ts_tenant");

        builder.HasOne(setting => setting.Installation)
            .WithMany(installation => installation.ToolSettings)
            .HasForeignKey(setting => setting.InstallationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
