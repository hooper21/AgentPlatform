using AgentPlatform.Core.Abstractions;
using AgentPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Infrastructure.Repositories;

/// <summary>
/// Репозиторий глобальных таблиц платформы (connector_registry, solution_profiles, tenants).
/// Tenant-фильтр не применяется. Tenant-сущности использовать запрещено —
/// для них есть <see cref="TenantScopedRepository{T}"/>.
/// </summary>
public sealed class GlobalRepository<T> : IGlobalRepository<T>
    where T : class
{
    private readonly AgentPlatformDbContext _context;

    public GlobalRepository(AgentPlatformDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (typeof(ITenantEntity).IsAssignableFrom(typeof(T)))
        {
            throw new InvalidOperationException(
                $"'{typeof(T).Name}' is a tenant-scoped entity: use ITenantScopedRepository<{typeof(T).Name}> " +
                "instead of the global repository.");
        }

        _context = context;
    }

    /// <inheritdoc />
    public IQueryable<T> Query => _context.Set<T>();

    /// <inheritdoc />
    public async Task<IReadOnlyList<T>> ListAsync(CancellationToken cancellationToken = default)
        => await Query.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<T?> FindAsync(object key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        return await _context.Set<T>()
            .FindAsync([key], cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<T> AddAsync(T entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        await _context.Set<T>().AddAsync(entity, cancellationToken).ConfigureAwait(false);
        return entity;
    }

    /// <inheritdoc />
    public void Update(T entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        _context.Set<T>().Update(entity);
    }

    /// <inheritdoc />
    public void Remove(T entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        _context.Set<T>().Remove(entity);
    }

    /// <inheritdoc />
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => _context.SaveChangesAsync(cancellationToken);
}
