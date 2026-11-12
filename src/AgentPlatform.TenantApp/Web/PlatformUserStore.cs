using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.TenantApp.Web;

/// <summary>
/// ASP.NET Identity store над существующей таблицей users (без новых таблиц).
/// Пароль хранится в users.password_hash (PBKDF2), роль — users.role.
/// <para>
/// Email уникален внутри тенанта (ux_users_tenant_email), поэтому вход ищет
/// пользователя по email; при одинаковом email в разных тенантах вход отклоняется
/// как неоднозначный (минимальная версия кабинета).
/// </para>
/// </summary>
public sealed class PlatformUserStore(AgentPlatformDbContext context) :
    IUserStore<User>,
    IUserPasswordStore<User>,
    IUserEmailStore<User>
{
    private readonly AgentPlatformDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public Task<IdentityResult> CreateAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        _context.Users.Add(user);
        return SaveAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IdentityResult> UpdateAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        _context.Users.Update(user);
        return await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IdentityResult> DeleteAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        _context.Users.Remove(user);
        return await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<User?> FindByIdAsync(string userId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(userId, out var id))
        {
            return Task.FromResult<User?>(null);
        }

        return _context.Users.FirstOrDefaultAsync(user => user.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public Task<User?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken)
    {
        var email = (normalizedUserName ?? string.Empty).Trim().ToLowerInvariant();

        if (email.Length == 0)
        {
            return Task.FromResult<User?>(null);
        }

        // Email уникален внутри тенанта; несколько совпадений = неоднозначный вход.
        return FindSingleAsync(_context.Users
            .Where(user => user.Email.ToLower() == email)
            .Take(2)
            .ToListAsync(cancellationToken));
    }

    /// <inheritdoc />
    public Task<string> GetUserIdAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        return Task.FromResult(user.Id.ToString("D"));
    }

    /// <inheritdoc />
    public Task<string?> GetUserNameAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        return Task.FromResult<string?>(user.Email);
    }

    /// <inheritdoc />
    public Task SetUserNameAsync(User user, string? userName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        user.Email = (userName ?? string.Empty).Trim().ToLowerInvariant();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string?> GetNormalizedUserNameAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        return Task.FromResult<string?>(user.Email.ToUpperInvariant());
    }

    /// <inheritdoc />
    public Task SetNormalizedUserNameAsync(User user, string? normalizedName, CancellationToken cancellationToken)
    {
        // Отдельного normalized_email в users нет: поиск идёт по lower(email).
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string?> GetPasswordHashAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        return Task.FromResult<string?>(user.PasswordHash);
    }

    /// <inheritdoc />
    public Task SetPasswordHashAsync(User user, string? passwordHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        user.PasswordHash = passwordHash ?? string.Empty;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> HasPasswordAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        return Task.FromResult(!string.IsNullOrEmpty(user.PasswordHash));
    }

    /// <inheritdoc />
    public Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
        => FindByNameAsync(normalizedEmail, cancellationToken);

    /// <inheritdoc />
    public Task<string?> GetEmailAsync(User user, CancellationToken cancellationToken)
        => GetUserNameAsync(user, cancellationToken);

    /// <inheritdoc />
    public Task SetEmailAsync(User user, string? email, CancellationToken cancellationToken)
        => SetUserNameAsync(user, email, cancellationToken);

    /// <inheritdoc />
    public Task<bool> GetEmailConfirmedAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        // Подтверждение email в v1 не используется: пользователей создаёт админ.
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task SetEmailConfirmedAsync(User user, bool confirmed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string?> GetNormalizedEmailAsync(User user, CancellationToken cancellationToken)
        => GetNormalizedUserNameAsync(user, cancellationToken);

    /// <inheritdoc />
    public Task SetNormalizedEmailAsync(User user, string? normalizedEmail, CancellationToken cancellationToken)
        => SetNormalizedUserNameAsync(user, normalizedEmail, cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        // DbContext принадлежит DI-скоупу.
    }

    private async Task<IdentityResult> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return IdentityResult.Success;
        }
        catch (DbUpdateException exception)
        {
            return IdentityResult.Failed(new IdentityError
            {
                Code = "persistence_error",
                Description = exception.InnerException?.Message ?? exception.Message
            });
        }
    }

    private static async Task<User?> FindSingleAsync(Task<List<User>> matchesTask)
    {
        var matches = await matchesTask.ConfigureAwait(false);

        // Неоднозначный email (несколько тенантов) трактуется как «пользователь не найден»:
        // вход не должен раскрывать состав тенантов.
        return matches.Count == 1 ? matches[0] : null;
    }
}
