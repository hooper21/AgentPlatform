using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Validation;
using AgentPlatform.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Infrastructure.Persistence;

/// <summary>
/// EF Core модель платформы (PostgreSQL, 18 таблиц из DDL задачи 1.1).
/// </summary>
public sealed class AgentPlatformDbContext(DbContextOptions<AgentPlatformDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<User> Users => Set<User>();

    public DbSet<ConnectorRegistryEntry> ConnectorRegistry => Set<ConnectorRegistryEntry>();

    public DbSet<ConnectorInstallation> ConnectorInstallations => Set<ConnectorInstallation>();

    public DbSet<ToolSetting> ToolSettings => Set<ToolSetting>();

    public DbSet<Agent> Agents => Set<Agent>();

    public DbSet<AgentVersion> AgentVersions => Set<AgentVersion>();

    public DbSet<Channel> Channels => Set<Channel>();

    public DbSet<KnowledgeSource> KnowledgeSources => Set<KnowledgeSource>();

    public DbSet<KnowledgeChunk> KnowledgeChunks => Set<KnowledgeChunk>();

    public DbSet<AgentSession> Sessions => Set<AgentSession>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<Checkpoint> Checkpoints => Set<Checkpoint>();

    public DbSet<Approval> Approvals => Set<Approval>();

    public DbSet<ToolAuditEntry> ToolAudit => Set<ToolAuditEntry>();

    public DbSet<UsageRecord> Usage => Set<UsageRecord>();

    public DbSet<ScheduledRun> ScheduledRuns => Set<ScheduledRun>();

    public DbSet<SolutionProfile> SolutionProfiles => Set<SolutionProfile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AgentPlatformDbContext).Assembly);
        SnakeCaseNamingConvention.Apply(modelBuilder.Model);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateBeforeSave();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        ValidateBeforeSave();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Инварианты, которые в v1 проверяются кодом, а не CHECK-ограничениями БД:
    /// tenant_id у вставляемых tenant-сущностей, append-only tool_audit,
    /// REQUIRED-валидация approval.gate_payload и agent_versions.rules.
    /// </summary>
    private void ValidateBeforeSave()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is EntityState.Detached or EntityState.Unchanged)
            {
                continue;
            }

            if (entry.State == EntityState.Added
                && entry.Entity is ITenantEntity tenantEntity
                && tenantEntity.TenantId == Guid.Empty)
            {
                throw new InvalidOperationException(
                    $"Table '{entry.Metadata.GetTableName()}': tenant_id is required for inserts. " +
                    "Use TenantScopedRepository<T>, которая проставляет tenant_id из ITenantContext.");
            }

            switch (entry.Entity)
            {
                case ToolAuditEntry when entry.State is EntityState.Modified or EntityState.Deleted:
                    throw new InvalidOperationException(
                        "tool_audit is append-only: UPDATE and DELETE are forbidden by platform rules.");

                case Approval approval when entry.State is EntityState.Added or EntityState.Modified:
                    GatePayloadValidator.Validate(approval.GatePayload);
                    break;

                case AgentVersion version when entry.State is EntityState.Added or EntityState.Modified:
                    AgentRulesValidator.Validate(version.Rules);
                    break;
            }
        }
    }
}
