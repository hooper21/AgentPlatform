using AgentPlatform.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentPlatform.Infrastructure.Persistence.Configurations;

/// <summary>tenants — справочник тенантов платформы.</summary>
public sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");
        builder.HasKey(tenant => tenant.Id);

        builder.Property(tenant => tenant.Name).HasMaxLength(200).IsRequired();
        builder.Property(tenant => tenant.Plan).HasMaxLength(50).IsRequired().HasDefaultValue("pilot");
        builder.Property(tenant => tenant.LlmConfig).IsJsonb(defaultSql: null);
        builder.Property(tenant => tenant.Status).HasMaxLength(20).IsRequired().HasDefaultValue("active");
        builder.Property(tenant => tenant.CreatedAt).IsTimestamp();
        builder.Property(tenant => tenant.UpdatedAt).IsTimestamp();
    }
}

/// <summary>users — пользователи кабинета тенанта, email уникален внутри тенанта.</summary>
public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(user => user.Id);

        builder.Property(user => user.Email).HasMaxLength(320).IsRequired();
        builder.Property(user => user.PasswordHash).HasMaxLength(500).IsRequired();
        builder.Property(user => user.Role).HasMaxLength(20).IsRequired();
        builder.Property(user => user.AuthMeta).IsJsonb();
        builder.Property(user => user.CreatedAt).IsTimestamp();
        builder.Property(user => user.UpdatedAt).IsTimestamp();

        builder.HasIndex(user => new { user.TenantId, user.Email }).IsUnique().HasDatabaseName("ux_users_tenant_email");
        builder.HasIndex(user => user.TenantId).HasDatabaseName("ix_users_tenant");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(user => user.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>connector_registry — каталог коннекторов платформы (глобальная таблица).</summary>
public sealed class ConnectorRegistryEntryConfiguration : IEntityTypeConfiguration<ConnectorRegistryEntry>
{
    public void Configure(EntityTypeBuilder<ConnectorRegistryEntry> builder)
    {
        builder.ToTable("connector_registry");
        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.Name).HasMaxLength(100).IsRequired();
        builder.Property(entry => entry.Version).HasMaxLength(20).IsRequired();
        builder.Property(entry => entry.Manifest).IsJsonb(defaultSql: null);
        builder.Property(entry => entry.ManifestHash).HasMaxLength(64).IsRequired();
        builder.Property(entry => entry.Publisher).HasMaxLength(100).IsRequired();
        builder.Property(entry => entry.Status).HasMaxLength(20).IsRequired().HasDefaultValue("published");
        builder.Property(entry => entry.CreatedAt).IsTimestamp();
        builder.Property(entry => entry.UpdatedAt).IsTimestamp();

        builder.HasIndex(entry => new { entry.Name, entry.Version }).IsUnique().HasDatabaseName("ux_connector_registry_name_version");
    }
}

/// <summary>solution_profiles — шаблоны решений (глобальная таблица платформы).</summary>
public sealed class SolutionProfileConfiguration : IEntityTypeConfiguration<SolutionProfile>
{
    public void Configure(EntityTypeBuilder<SolutionProfile> builder)
    {
        builder.ToTable("solution_profiles");
        builder.HasKey(profile => profile.Id);

        builder.Property(profile => profile.Name).HasMaxLength(200).IsRequired();
        builder.Property(profile => profile.Version).HasMaxLength(20).IsRequired();
        builder.Property(profile => profile.Config).IsJsonb(defaultSql: null);
        builder.Property(profile => profile.Publisher).HasMaxLength(100).IsRequired();
        builder.Property(profile => profile.CreatedAt).IsTimestamp();
        builder.Property(profile => profile.UpdatedAt).IsTimestamp();

        builder.HasIndex(profile => new { profile.Name, profile.Version }).IsUnique().HasDatabaseName("ux_solution_profiles_name_version");
    }
}
