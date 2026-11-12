using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Infrastructure.Repositories;

/// <summary>
/// Расписания планировщика в PostgreSQL (порт <see cref="IScheduledRunStore"/>).
/// <para>
/// Все операции явно фильтруются по tenant_id: даже при ошибке в вызывающем коде
/// расписание другого тенанта не будет прочитано или изменено.
/// </para>
/// </summary>
public sealed class EfScheduledRunStore(AgentPlatformDbContext context) : IScheduledRunStore
{
    private readonly AgentPlatformDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScheduledRunInfo>> ListAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);

        var runs = await _context.ScheduledRuns
            .AsNoTracking()
            .Where(run => run.TenantId == tenantId)
            .OrderBy(run => run.CreatedAt)
            .ThenBy(run => run.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return runs.Select(Map).ToList();
    }

    /// <inheritdoc />
    public async Task<ScheduledRunInfo?> FindAsync(
        Guid tenantId,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);

        var run = await _context.ScheduledRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.TenantId == tenantId && candidate.Id == id,
                cancellationToken)
            .ConfigureAwait(false);

        return run is null ? null : Map(run);
    }

    /// <inheritdoc />
    public async Task<ScheduledRunInfo> CreateAsync(
        Guid tenantId,
        Guid agentId,
        string cronExpression,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireAgent(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(cronExpression);

        var now = DateTimeOffset.UtcNow;
        var run = new ScheduledRun
        {
            TenantId = tenantId,
            AgentId = agentId,
            CronExpression = cronExpression.Trim(),
            Enabled = enabled,
            CreatedAt = now,
            UpdatedAt = now
        };

        _context.ScheduledRuns.Add(run);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Map(run);
    }

    /// <inheritdoc />
    public async Task<ScheduledRunInfo?> UpdateAsync(
        Guid tenantId,
        Guid id,
        Guid agentId,
        string cronExpression,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireAgent(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(cronExpression);

        var run = await FindTrackedAsync(tenantId, id, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return null;
        }

        run.AgentId = agentId;
        run.CronExpression = cronExpression.Trim();
        run.Enabled = enabled;
        run.UpdatedAt = DateTimeOffset.UtcNow;

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Map(run);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        Guid tenantId,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);

        var run = await FindTrackedAsync(tenantId, id, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return false;
        }

        _context.ScheduledRuns.Remove(run);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <inheritdoc />
    public async Task MarkRunAsync(
        Guid tenantId,
        Guid id,
        DateTimeOffset lastRunAt,
        ScheduledRunStatus status,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);

        var run = await FindTrackedAsync(tenantId, id, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return;
        }

        run.LastRunAt = lastRunAt;
        run.LastRunStatus = status.ToDbValue();
        run.UpdatedAt = DateTimeOffset.UtcNow;

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<ScheduledRun?> FindTrackedAsync(Guid tenantId, Guid id, CancellationToken cancellationToken)
        => _context.ScheduledRuns.FirstOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.Id == id,
            cancellationToken);

    private static void RequireTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("tenant_id is required", nameof(tenantId));
        }
    }

    private static void RequireAgent(Guid agentId)
    {
        if (agentId == Guid.Empty)
        {
            throw new ArgumentException("agent_id is required", nameof(agentId));
        }
    }

    private static ScheduledRunInfo Map(ScheduledRun run) => new(
        run.Id,
        run.TenantId,
        run.AgentId,
        run.CronExpression,
        run.Enabled,
        run.LastRunAt,
        run.LastRunStatus,
        run.CreatedAt,
        run.UpdatedAt);
}
