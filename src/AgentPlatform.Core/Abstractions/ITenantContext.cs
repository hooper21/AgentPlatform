namespace AgentPlatform.Core.Abstractions;

/// <summary>
/// Текущий тенант запроса. Регистрируется в DI как scoped и заполняется
/// из аутентификации (в v1 — заглушкой по заголовку X-Tenant-Id).
/// </summary>
public interface ITenantContext
{
    /// <summary>Идентификатор тенанта или null, если контекст ещё не установлен.</summary>
    Guid? TenantId { get; }

    /// <summary>true, если контекст установлен.</summary>
    bool IsResolved { get; }
}

/// <summary>Хелперы доступа к текущему тенанту.</summary>
public static class TenantContextExtensions
{
    /// <summary>
    /// Возвращает tenant_id или бросает <see cref="InvalidOperationException"/>,
    /// если тенант не установлен. Используется всеми tenant-фильтрами.
    /// </summary>
    public static Guid RequireTenantId(this ITenantContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.TenantId
            ?? throw new InvalidOperationException(
                "Tenant context is not resolved: tenant_id is required for this operation.");
    }
}
