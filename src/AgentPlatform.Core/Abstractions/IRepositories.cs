namespace AgentPlatform.Core.Abstractions;

/// <summary>
/// Репозиторий сущностей, изолированных по тенанту.
/// Все запросы автоматически фильтруются по tenant_id из <see cref="ITenantContext"/>.
/// </summary>
/// <typeparam name="T">Сущность, реализующая <see cref="ITenantEntity"/>.</typeparam>
public interface ITenantScopedRepository<T>
    where T : class, ITenantEntity
{
    /// <summary>Запрос с уже применённым фильтром по tenant_id.</summary>
    IQueryable<T> Query { get; }

    /// <summary>Список сущностей текущего тенанта.</summary>
    Task<IReadOnlyList<T>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Поиск по первичному ключу в пределах тенанта.
    /// Возвращает null, если сущность принадлежит другому тенанту.
    /// </summary>
    Task<T?> FindAsync(object key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Добавление сущности. tenant_id проставляется из контекста;
    /// без установленного контекста бросает <see cref="InvalidOperationException"/>.
    /// </summary>
    Task<T> AddAsync(T entity, CancellationToken cancellationToken = default);

    /// <summary>Добавление набора сущностей с проверкой tenant-принадлежности.</summary>
    Task AddRangeAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default);

    /// <summary>Пометка сущности как изменённой (с проверкой tenant-принадлежности).</summary>
    void Update(T entity);

    /// <summary>Пометка сущности как удалённой (с проверкой tenant-принадлежности).</summary>
    void Remove(T entity);

    /// <summary>Сохранение изменений в БД.</summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Репозиторий глобальных (платформенных) таблиц без tenant_id:
/// connector_registry, solution_profiles, tenants.
/// Tenant-фильтр не применяется. Tenant-сущности в нём запрещены.
/// </summary>
/// <typeparam name="T">Глобальная сущность.</typeparam>
public interface IGlobalRepository<T>
    where T : class
{
    /// <summary>Запрос без tenant-фильтра.</summary>
    IQueryable<T> Query { get; }

    /// <summary>Все строки таблицы.</summary>
    Task<IReadOnlyList<T>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Поиск по первичному ключу.</summary>
    Task<T?> FindAsync(object key, CancellationToken cancellationToken = default);

    /// <summary>Добавление строки.</summary>
    Task<T> AddAsync(T entity, CancellationToken cancellationToken = default);

    /// <summary>Пометка строки как изменённой.</summary>
    void Update(T entity);

    /// <summary>Пометка строки как удалённой.</summary>
    void Remove(T entity);

    /// <summary>Сохранение изменений в БД.</summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
