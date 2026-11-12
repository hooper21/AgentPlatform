using AgentPlatform.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Identity;
using PlatformPasswordHasher = AgentPlatform.Infrastructure.Seeding.IPasswordHasher;

namespace AgentPlatform.TenantApp.Web;

/// <summary>
/// ASP.NET Identity password hasher поверх существующего PBKDF2 (users.password_hash).
/// Формат хеша не меняется: seed и кабинет используют один и тот же алгоритм.
/// </summary>
public sealed class PlatformIdentityPasswordHasher(PlatformPasswordHasher hasher)
    : IPasswordHasher<User>
{
    private readonly PlatformPasswordHasher _hasher =
        hasher ?? throw new ArgumentNullException(nameof(hasher));

    /// <inheritdoc />
    public string HashPassword(User user, string password)
    {
        ArgumentNullException.ThrowIfNull(user);

        return _hasher.Hash(password);
    }

    /// <inheritdoc />
    public PasswordVerificationResult VerifyHashedPassword(
        User user,
        string hashedPassword,
        string providedPassword)
    {
        ArgumentNullException.ThrowIfNull(user);

        return _hasher.Verify(providedPassword, hashedPassword)
            ? PasswordVerificationResult.Success
            : PasswordVerificationResult.Failed;
    }
}
