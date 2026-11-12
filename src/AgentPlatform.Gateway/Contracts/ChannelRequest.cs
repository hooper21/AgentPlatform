using AgentPlatform.Gateway.Sessions;

namespace AgentPlatform.Gateway.Contracts;

/// <summary>
/// Данные запроса канала, которые нужны Gateway для аутентификации и идентификации
/// визитера. Извлекаются из HTTP-запроса на границе слоя, чтобы бизнес-логика
/// (pipeline) не зависела от HttpContext и могла тестироваться без сервера.
/// </summary>
/// <param name="WidgetToken">JWT виджета (Authorization: Bearer или ?access_token для SSE).</param>
/// <param name="VisitorCookieRef">user_ref из cookie (ставится Gateway при первом визите).</param>
/// <param name="UserRef">user_ref из localStorage виджета (заголовок X-User-Ref).</param>
public sealed record ChannelRequest(
    string? WidgetToken,
    string? VisitorCookieRef = null,
    string? UserRef = null)
{
    /// <summary>Заголовок с user_ref из localStorage виджета.</summary>
    public const string UserRefHeader = "X-User-Ref";

    /// <summary>Параметр строки запроса с токеном: EventSource не умеет ставить заголовки.</summary>
    public const string AccessTokenQuery = "access_token";

    /// <summary>Собирает данные канала из HTTP-запроса.</summary>
    public static ChannelRequest From(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new ChannelRequest(
            WidgetToken: ReadToken(request),
            VisitorCookieRef: request.Cookies[VisitorIdentityResolver.CookieName],
            UserRef: request.Headers[UserRefHeader].FirstOrDefault());
    }

    private static string? ReadToken(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(authorization)
            && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authorization["Bearer ".Length..].Trim();
        }

        var queryToken = request.Query[AccessTokenQuery].FirstOrDefault();

        return string.IsNullOrWhiteSpace(queryToken) ? null : queryToken.Trim();
    }
}
