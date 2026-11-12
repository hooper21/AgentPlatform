using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentPlatform.Gateway.Errors;

namespace AgentPlatform.Gateway.Security;

/// <summary>
/// Проверка JWT виджета (HS256, секрет на тенанта). Реализовано на
/// System.Security.Cryptography без новых зависимостей: платформа принимает только
/// HS256, подпись сверяется за постоянное время, срок действия обязателен.
/// <para>
/// Порядок проверки: alg → claim tenant_key → срок действия → подпись секретом
/// тенанта. tenant_key читается до проверки подписи только чтобы выбрать ключ;
/// после проверки токен привязан к этому тенанту.
/// </para>
/// </summary>
public sealed class WidgetJwtAuthenticator(IWidgetSecretProvider secretProvider)
{
    /// <summary>Claim с ключом тенанта (uuid).</summary>
    public const string TenantKeyClaim = "tenant_key";

    /// <summary>Claim с идентификатором визитера.</summary>
    public const string UserRefClaim = "user_ref";

    /// <summary>Claim-фолбэк для визитера (стандартный sub).</summary>
    public const string SubjectClaim = "sub";

    /// <summary>Единственный допустимый алгоритм подписи.</summary>
    public const string Algorithm = "HS256";

    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);

    private readonly IWidgetSecretProvider _secretProvider =
        secretProvider ?? throw new ArgumentNullException(nameof(secretProvider));

    /// <summary>Проверяет токен, выпущенный для тенанта из маршрута (tenantKey).</summary>
    /// <exception cref="GatewayException">401: токена нет, он повреждён, просрочен, подпись неверна.</exception>
    public WidgetIdentity Authenticate(string tenantKey, string? token)
    {
        if (string.IsNullOrWhiteSpace(tenantKey))
        {
            throw GatewayException.BadRequest("tenant_key_required", "tenant_key is required in the route.");
        }

        return Verify(token, tenantKey.Trim());
    }

    /// <summary>
    /// Проверяет токен и берёт тенант из claim tenant_key. Нужно для SSE:
    /// EventSource не умеет передавать заголовки, tenant в пути отсутствует.
    /// </summary>
    /// <exception cref="GatewayException">401: токен отсутствует или невалиден.</exception>
    public WidgetIdentity Authenticate(string? token) => Verify(token, expectedTenantKey: null);

    private WidgetIdentity Verify(string? token, string? expectedTenantKey)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw GatewayException.Unauthorized("widget JWT is required (Authorization: Bearer <token>).");
        }

        var parts = token.Trim().Split('.');
        if (parts.Length != 3)
        {
            throw GatewayException.Unauthorized("widget JWT must consist of three parts.");
        }

        using var header = ParseJson(parts[0], "header");
        if (!header.RootElement.TryGetProperty("alg", out var algorithm)
            || !string.Equals(algorithm.GetString(), Algorithm, StringComparison.Ordinal))
        {
            throw GatewayException.Unauthorized("widget JWT must be signed with HS256.");
        }

        using var payload = ParseJson(parts[1], "payload");

        var claimTenantKey = ReadString(payload.RootElement, TenantKeyClaim)
            ?? throw GatewayException.Unauthorized($"widget JWT claim '{TenantKeyClaim}' is required.");

        if (!Guid.TryParse(claimTenantKey, out var tenantId))
        {
            throw GatewayException.Unauthorized($"widget JWT claim '{TenantKeyClaim}' must be a tenant id (uuid).");
        }

        if (expectedTenantKey is not null && !TenantKeysMatch(expectedTenantKey, tenantId))
        {
            throw GatewayException.Unauthorized("widget JWT belongs to another tenant.");
        }

        ValidateLifetime(payload.RootElement);

        var secret = _secretProvider.GetSecret(tenantId)
            ?? throw GatewayException.Unauthorized("widget channel is not configured for this tenant.");

        var expectedSignature = Sign(parts[0], parts[1], secret);
        var actualSignature = Base64UrlDecode(parts[2]);

        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, actualSignature))
        {
            throw GatewayException.Unauthorized("widget JWT signature is invalid.");
        }

        var userRef = ReadString(payload.RootElement, UserRefClaim)
            ?? ReadString(payload.RootElement, SubjectClaim);

        return new WidgetIdentity(tenantId, tenantId.ToString("D"), userRef);
    }

    private static bool TenantKeysMatch(string expectedTenantKey, Guid tenantId)
        => Guid.TryParse(expectedTenantKey, out var expected)
            ? expected == tenantId
            : string.Equals(expectedTenantKey, tenantId.ToString("D"), StringComparison.OrdinalIgnoreCase);

    private static void ValidateLifetime(JsonElement payload)
    {
        var now = DateTimeOffset.UtcNow;

        if (!payload.TryGetProperty("exp", out var expiresAt)
            || !expiresAt.TryGetInt64(out var expiresAtSeconds))
        {
            throw GatewayException.Unauthorized("widget JWT claim 'exp' is required.");
        }

        if (now > FromUnixSeconds(expiresAtSeconds) + ClockSkew)
        {
            throw GatewayException.Unauthorized("widget JWT is expired.");
        }

        if (payload.TryGetProperty("nbf", out var notBefore)
            && notBefore.TryGetInt64(out var notBeforeSeconds)
            && now + ClockSkew < FromUnixSeconds(notBeforeSeconds))
        {
            throw GatewayException.Unauthorized("widget JWT is not active yet.");
        }
    }

    private static DateTimeOffset FromUnixSeconds(long seconds)
        => seconds is < 0 or > 253402300799
            ? throw GatewayException.Unauthorized("widget JWT lifetime claim is out of range.")
            : DateTimeOffset.FromUnixTimeSeconds(seconds);

    private static byte[] Sign(string headerPart, string payloadPart, string secret)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var data = Encoding.ASCII.GetBytes($"{headerPart}.{payloadPart}");

        return HMACSHA256.HashData(key, data);
    }

    private static JsonDocument ParseJson(string part, string name)
    {
        try
        {
            return JsonDocument.Parse(Base64UrlDecode(part));
        }
        catch (JsonException exception)
        {
            throw GatewayException.Unauthorized($"widget JWT {name} is not a valid JSON document: {exception.Message}");
        }
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');

        base64 = (base64.Length % 4) switch
        {
            0 => base64,
            2 => base64 + "==",
            3 => base64 + "=",
            _ => throw GatewayException.Unauthorized("widget JWT is not base64url encoded.")
        };

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw GatewayException.Unauthorized("widget JWT is not base64url encoded.");
        }
    }

    private static string? ReadString(JsonElement payload, string claim)
        => payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(claim, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
}
