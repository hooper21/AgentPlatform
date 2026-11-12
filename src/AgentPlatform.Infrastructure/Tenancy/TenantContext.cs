using AgentPlatform.Core.Abstractions;

namespace AgentPlatform.Infrastructure.Tenancy;

/// <summary>
/// Scoped-реализация <see cref="ITenantContext"/>. Заполняется middleware
/// аутентификации (в v1 — заглушкой по заголовку X-Tenant-Id).
/// </summary>
public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }

    public bool IsResolved => TenantId.HasValue;

    /// <summary>Устанавливает текущего тенанта запроса.</summary>
    public void SetTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("tenant_id must not be empty.", nameof(tenantId));
        }

        TenantId = tenantId;
    }

    /// <summary>Сбрасывает контекст (используется в тестах и фоновых операциях).</summary>
    public void Clear() => TenantId = null;
}
