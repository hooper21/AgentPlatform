using AgentPlatform.Infrastructure.Tenancy;

namespace AgentPlatform.TenantApp.Web;

/// <summary>
/// Заглушка аутентификации: tenant_id берётся из заголовка X-Tenant-Id.
/// TODO(auth): заменить на claim tenant_id из JWT кабинета (задача 2.x),
/// заголовок должен игнорироваться при наличии токена.
/// </summary>
public sealed class TenantHeaderMiddleware(RequestDelegate next)
{
    /// <summary>Заголовок с идентификатором тенанта (только для v1-заглушки).</summary>
    public const string HeaderName = "X-Tenant-Id";

    public async Task InvokeAsync(HttpContext context, TenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenantContext);

        var rawTenantId = context.Request.Headers[HeaderName].FirstOrDefault();
        if (Guid.TryParse(rawTenantId, out var tenantId))
        {
            tenantContext.SetTenant(tenantId);
        }

        await next(context).ConfigureAwait(false);
    }
}
