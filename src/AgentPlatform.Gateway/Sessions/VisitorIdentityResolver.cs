using AgentPlatform.Gateway.Contracts;

namespace AgentPlatform.Gateway.Sessions;

/// <summary>Идентификация визитера: стабильный user_ref и признак первого визита.</summary>
/// <param name="UserRef">Идентификатор визитера (до 200 символов — sessions.user_ref).</param>
/// <param name="IsNew">true — идентификатор выдан Gateway, каналу нужно выставить cookie.</param>
public sealed record VisitorIdentity(string UserRef, bool IsNew);

/// <summary>
/// Идентификация визитера канала (cookie / localStorage → user_ref).
/// <para>
/// Приоритет источников: claim подписанного JWT (доверенный) → user_ref из
/// localStorage виджета (заголовок X-User-Ref) → cookie, выданная Gateway.
/// Если ничего нет, генерируется новый идентификатор и cookie выставляется ответом.
/// localStorage-значение не подписано: это идентификатор диалога, а не авторизация,
/// поэтому подделка чужого user_ref не даёт доступа к чужим данным сессии
/// (сессия дополнительно привязана к тенанту токена).
/// </para>
/// </summary>
public static class VisitorIdentityResolver
{
    /// <summary>Cookie с user_ref визитера.</summary>
    public const string CookieName = "ap_user_ref";

    /// <summary>Максимальная длина user_ref (sessions.user_ref = varchar(200)).</summary>
    public const int MaxLength = 200;

    /// <summary>Определяет user_ref визитера по данным канала.</summary>
    public static VisitorIdentity Resolve(
        string? tokenUserRef,
        string? localUserRef,
        string? cookieUserRef)
    {
        foreach (var candidate in new[] { tokenUserRef, localUserRef, cookieUserRef })
        {
            if (!IsValid(candidate))
            {
                continue;
            }

            return new VisitorIdentity(candidate!.Trim(), IsNew: false);
        }

        return new VisitorIdentity($"v-{Guid.NewGuid():N}", IsNew: true);
    }

    /// <summary>Настройки cookie идентификатора визитера.</summary>
    public static CookieOptions CreateCookieOptions(bool secure) => new()
    {
        HttpOnly = true,
        Secure = secure,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        MaxAge = TimeSpan.FromDays(365)
    };

    private static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();

        return trimmed.Length <= MaxLength && !trimmed.Any(char.IsControl);
    }
}
