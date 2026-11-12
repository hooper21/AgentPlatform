using System.Security.Claims;

namespace AgentPlatform.TenantApp.Web;

/// <summary>Claims кабинета: tenant_id и роль из таблицы users (ASP.NET Identity).</summary>
public static class CabinetClaims
{
    /// <summary>Claim тенанта (заполняется claims factory при входе).</summary>
    public const string TenantId = "tenant_id";

    /// <summary>Claim роли кабинета: admin | operator | viewer.</summary>
    public const string Role = "cabinet_role";

    /// <summary>Claim email пользователя.</summary>
    public const string Email = "cabinet_email";

    /// <summary>Роли кабинета.</summary>
    public static class Roles
    {
        public const string Admin = "admin";
        public const string Operator = "operator";
        public const string Viewer = "viewer";
    }

    /// <summary>Возвращает tenant_id из claim или null.</summary>
    public static Guid? GetTenantId(ClaimsPrincipal? principal)
    {
        var raw = principal?.FindFirst(TenantId)?.Value;

        return Guid.TryParse(raw, out var tenantId) ? tenantId : null;
    }

    /// <summary>Возвращает роль кабинета (по умолчанию viewer).</summary>
    public static string GetRole(ClaimsPrincipal? principal)
        => principal?.FindFirst(Role)?.Value ?? Roles.Viewer;

    /// <summary>Может ли пользователь изменять конфигурацию (admin).</summary>
    public static bool CanManage(ClaimsPrincipal? principal)
        => string.Equals(GetRole(principal), Roles.Admin, StringComparison.Ordinal);

    /// <summary>Может ли пользователь принимать решения по гейтам (admin/operator).</summary>
    public static bool CanDecide(ClaimsPrincipal? principal)
    {
        var role = GetRole(principal);

        return string.Equals(role, Roles.Admin, StringComparison.Ordinal)
            || string.Equals(role, Roles.Operator, StringComparison.Ordinal);
    }
}
