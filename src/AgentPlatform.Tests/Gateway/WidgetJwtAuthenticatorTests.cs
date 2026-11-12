using System.Text;
using AgentPlatform.Gateway.Errors;
using AgentPlatform.Gateway.Security;

namespace AgentPlatform.Tests.Gateway;

/// <summary>
/// Проверка JWT виджета: алгоритм, привязка tenant_key к маршруту, срок действия,
/// подпись секретом тенанта (HS256).
/// </summary>
public sealed class WidgetJwtAuthenticatorTests
{
    private static readonly Guid TenantId = GatewayHarness.TenantId;
    private static readonly Guid OtherTenantId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private const string Secret = GatewayHarness.WidgetSecret;
    private const string OtherSecret = "other-widget-secret-0123456789abcdef";

    [Fact]
    public void Valid_token_is_accepted_and_user_ref_is_returned()
    {
        var token = GatewayHarness.CreateWidgetToken(TenantId, Secret, userRef: "visitor-7");

        var identity = CreateAuthenticator().Authenticate(TenantId.ToString("D"), token);

        Assert.Equal(TenantId, identity.TenantId);
        Assert.Equal("visitor-7", identity.UserRef);
    }

    [Fact]
    public void Tenant_is_taken_from_claim_when_route_key_is_absent()
    {
        var token = GatewayHarness.CreateWidgetToken(TenantId, Secret);

        var identity = CreateAuthenticator().Authenticate(token);

        Assert.Equal(TenantId, identity.TenantId);
        Assert.Null(identity.UserRef);
    }

    [Fact]
    public void Token_of_another_tenant_is_rejected()
    {
        var token = GatewayHarness.CreateWidgetToken(OtherTenantId, OtherSecret);

        var exception = Assert.Throws<GatewayException>(
            () => CreateAuthenticator().Authenticate(TenantId.ToString("D"), token));

        Assert.Equal(401, exception.StatusCode);
        Assert.Equal("unauthorized", exception.Code);
    }

    [Fact]
    public void Token_signed_with_wrong_secret_is_rejected()
    {
        var token = GatewayHarness.CreateWidgetToken(TenantId, "foreign-secret-0123456789abcdef");

        var exception = Assert.Throws<GatewayException>(
            () => CreateAuthenticator().Authenticate(TenantId.ToString("D"), token));

        Assert.Equal(401, exception.StatusCode);
    }

    [Fact]
    public void Expired_token_is_rejected()
    {
        var token = GatewayHarness.CreateWidgetToken(TenantId, Secret, lifetime: TimeSpan.FromMinutes(-10));

        var exception = Assert.Throws<GatewayException>(
            () => CreateAuthenticator().Authenticate(TenantId.ToString("D"), token));

        Assert.Equal(401, exception.StatusCode);
    }

    [Fact]
    public void Token_with_alg_none_is_rejected()
    {
        var header = Base64Url("""{"alg":"none","typ":"JWT"}""");
        var payload = Base64Url(
            $$"""{"tenant_key":"{{TenantId}}","exp":{{DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()}}}""");

        var exception = Assert.Throws<GatewayException>(
            () => CreateAuthenticator().Authenticate(TenantId.ToString("D"), $"{header}.{payload}."));

        Assert.Equal(401, exception.StatusCode);
    }

    [Fact]
    public void Missing_token_is_rejected()
    {
        var exception = Assert.Throws<GatewayException>(
            () => CreateAuthenticator().Authenticate(TenantId.ToString("D"), token: null));

        Assert.Equal(401, exception.StatusCode);
    }

    [Fact]
    public void Tenant_without_configured_secret_is_rejected()
    {
        var token = GatewayHarness.CreateWidgetToken(OtherTenantId, OtherSecret);
        var authenticator = new WidgetJwtAuthenticator(new FixedWidgetSecretProvider((TenantId, Secret)));

        var exception = Assert.Throws<GatewayException>(() => authenticator.Authenticate(token));

        Assert.Equal(401, exception.StatusCode);
    }

    private static WidgetJwtAuthenticator CreateAuthenticator()
        => new(new FixedWidgetSecretProvider((TenantId, Secret), (OtherTenantId, OtherSecret)));

    private static string Base64Url(string json)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
