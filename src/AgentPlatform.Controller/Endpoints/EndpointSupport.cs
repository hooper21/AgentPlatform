using AgentPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Controller.Endpoints;

/// <summary>Общие проверки Controller API (tenant-first маршруты, единый формат ошибок).</summary>
internal static class EndpointSupport
{
    /// <summary>Существует ли тенант (tenants — глобальная таблица платформы).</summary>
    public static Task<bool> TenantExistsAsync(
        AgentPlatformDbContext context,
        Guid tenantId,
        CancellationToken cancellationToken)
        => context.Tenants.AsNoTracking().AnyAsync(tenant => tenant.Id == tenantId, cancellationToken);

    /// <summary>404 для неизвестного тенанта.</summary>
    public static IResult TenantNotFound(Guid tenantId)
        => Results.NotFound(new { error = "tenant_not_found", tenantId });

    /// <summary>400 для некорректного тела/параметров.</summary>
    public static IResult InvalidRequest(string code, string message)
        => Results.BadRequest(new { error = code, message });
}
