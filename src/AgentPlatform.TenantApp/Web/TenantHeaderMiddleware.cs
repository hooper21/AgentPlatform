using AgentPlatform.Infrastructure.Tenancy;

namespace AgentPlatform.TenantApp.Web;

/// <summary>
///Устанавливает tenant_id запроса из claim пользователя кабинета (cookie Identity).
/// Заголовок X-Tenant-Id принимается только для анонимных dev/smoke-запросов:
/// при наличии аутентификации он игнорируется, подменить тенант нельзя.
/// </summary>
public sealed class TenantHeaderMiddleware(RequestDelegate next)
{
    /// <summary>Dev-фолбэк: заголовок с идентификатором тенанта.</summary>
    public const string HeaderName = "X-Tenant-Id";

    public async Task InvokeAsync(HttpContext context, TenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenantContext);

        if (context.User.Identity?.IsAuthenticated == true)
        {
            var claim = context.User.FindFirst(CabinetClaims.TenantId)?.Value;
            if (Guid.TryParse(claim, out var claimTenantId))
            {
                tenantContext.SetTenant(claimTenantId);
            }

            await next(context).ConfigureAwait(false);
            return;
        }

        var rawTenantId = context.Request.Headers[HeaderName].FirstOrDefault();
        if (Guid.TryParse(rawTenantId, out var tenantId))
        {
            tenantContext.SetTenant(tenantId);
        }

        await next(context).ConfigureAwait(false);
    }
}
