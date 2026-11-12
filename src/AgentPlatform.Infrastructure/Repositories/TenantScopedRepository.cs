using AgentPlatform.Core.Abstractions;
using AgentPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Infrastructure.Repositories;

/// <summary>
/// Репозиторий с автоматической tenant-изоляцией: во все запросы добавляется
/// фильтр по tenant_id из <see cref="ITenantContext"/>.
/// </summary>
public sealed class TenantScopedRepository<T> : ITenantScopedRepository<T>
    where T : class, ITenantEntity
{
    private readonly AgentPlatformDbContext _context;
    private readonly ITenantContext _tenantContext;

    public TenantScopedRepository(AgentPlatformDbContext context, ITenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenantContext);

        _context = context;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc />
    public IQueryable<T> Query
    {
        get
        {
            var tenantId = _tenantContext.RequireTenantId();
            return _context.Set<T>().Where(entity => entity.TenantId == tenantId);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<T>> ListAsync(CancellationToken cancellationToken = default)
        => await Query.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<T?> FindAsync(object key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        var tenantId = _tenantContext.RequireTenantId();
        var entity = await _context.Set<T>()
            .FindAsync([key], cancellationToken)
            .ConfigureAwait(false);

        return entity is not null && entity.TenantId == tenantId ? entity : null;
    }

    /// <inheritdoc />
    public async Task<T> AddAsync(T entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        AttachTenant(entity);
        await _context.Set<T>().AddAsync(entity, cancellationToken).ConfigureAwait(false);
        return entity;
    }

    /// <inheritdoc />
    public async Task AddRangeAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var items = entities as IReadOnlyCollection<T> ?? entities.ToList();
        foreach (var entity in items)
        {
            ArgumentNullException.ThrowIfNull(entity);
            AttachTenant(entity);
        }

        await _context.Set<T>().AddRangeAsync(items, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Update(T entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        EnsureSameTenant(entity);
        _context.Set<T>().Update(entity);
    }

    /// <inheritdoc />
    public void Remove(T entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        EnsureSameTenant(entity);
        _context.Set<T>().Remove(entity);
    }

    /// <inheritdoc />
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => _context.SaveChangesAsync(cancellationToken);

    private void AttachTenant(T entity)
    {
        var tenantId = _tenantContext.RequireTenantId();

        if (entity.TenantId == Guid.Empty)
        {
            entity.TenantId = tenantId;
            return;
        }

        if (entity.TenantId != tenantId)
        {
            throw new InvalidOperationException(
                $"Cross-tenant insert is forbidden: entity tenant_id {entity.TenantId} " +
                $"does not match current tenant {tenantId}.");
        }
    }

    private void EnsureSameTenant(T entity)
    {
        var tenantId = _tenantContext.RequireTenantId();

        if (entity.TenantId != tenantId)
        {
            throw new InvalidOperationException(
                $"Cross-tenant modification is forbidden: entity tenant_id {entity.TenantId} " +
                $"does not match current tenant {tenantId}.");
        }
    }
}
