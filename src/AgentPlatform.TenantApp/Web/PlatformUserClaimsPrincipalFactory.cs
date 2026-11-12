using System.Security.Claims;
using AgentPlatform.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace AgentPlatform.TenantApp.Web;

/// <summary>
/// Claims пользователя кабинета: стандартные claims Identity + tenant_id и роль.
/// Роль добавляется в <see cref="ClaimTypes.Role"/>, поэтому [Authorize(Roles=...)]
/// и AuthorizeView Roles работают без отдельного RoleStore.
/// </summary>
public sealed class PlatformUserClaimsPrincipalFactory(
    UserManager<User> userManager,
    IOptions<IdentityOptions> optionsAccessor)
    : UserClaimsPrincipalFactory<User>(userManager, optionsAccessor)
{
    /// <inheritdoc />
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var identity = await base.GenerateClaimsAsync(user).ConfigureAwait(false);

        identity.AddClaim(new Claim(CabinetClaims.TenantId, user.TenantId.ToString("D")));
        identity.AddClaim(new Claim(CabinetClaims.Role, user.Role));
        identity.AddClaim(new Claim(CabinetClaims.Email, user.Email));
        identity.AddClaim(new Claim(ClaimTypes.Role, user.Role));

        return identity;
    }
}
