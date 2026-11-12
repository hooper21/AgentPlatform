using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Json;
using AgentPlatform.Infrastructure.Configuration;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentPlatform.Infrastructure.Seeding;

/// <summary>
/// Проверочный seed: один тестовый тенант + admin-пользователь.
/// Идемпотентен: повторный запуск не создаёт дублей.
/// Пароль берётся только из переменной окружения <see cref="SeedOptions.PasswordEnvironmentVariable"/>.
/// </summary>
public sealed class DatabaseSeeder(
    AgentPlatformDbContext context,
    IPasswordHasher passwordHasher,
    SeedOptions options,
    ILogger<DatabaseSeeder> logger)
{
    /// <summary>Создаёт тенант и admin-пользователя, если их ещё нет.</summary>
    public async Task<SeedResult> SeedAsync(CancellationToken cancellationToken = default)
    {
        ValidateOptions();

        var tenant = await context.Tenants
            .FirstOrDefaultAsync(candidate => candidate.Name == options.TenantName, cancellationToken)
            .ConfigureAwait(false);

        var tenantCreated = false;
        if (tenant is null)
        {
            tenant = new Tenant
            {
                Name = options.TenantName,
                Plan = options.TenantPlan,
                LlmConfig = PlatformJson.SerializeDb(new TenantLlmConfig(LlmMode.Platform, "platform-default")),
                Status = TenantStatus.Active.ToDbValue()
            };
            context.Tenants.Add(tenant);
            tenantCreated = true;
        }

        var email = options.AdminEmail.Trim().ToLowerInvariant();
        var user = await context.Users
            .FirstOrDefaultAsync(
                candidate => candidate.TenantId == tenant.Id && candidate.Email == email,
                cancellationToken)
            .ConfigureAwait(false);

        var userCreated = false;
        if (user is null)
        {
            user = new User
            {
                TenantId = tenant.Id,
                Email = email,
                PasswordHash = passwordHasher.Hash(options.AdminPassword!),
                Role = UserRole.Admin.ToDbValue(),
                AuthMeta = "{}"
            };
            context.Users.Add(user);
            userCreated = true;
        }

        var roleUsersCreated = await EnsureRoleUsersAsync(tenant.Id, cancellationToken).ConfigureAwait(false);

        if (tenantCreated || userCreated || roleUsersCreated > 0)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Seed complete: tenant '{TenantName}' created={TenantCreated}, admin '{AdminEmail}' created={UserCreated}",
                options.TenantName,
                tenantCreated,
                email,
                userCreated);
        }
        else
        {
            logger.LogInformation("Seed skipped: tenant '{TenantName}' and admin '{AdminEmail}' already exist", options.TenantName, email);
        }

        return new SeedResult(tenant.Id, user.Id, tenantCreated, userCreated);
    }

    /// <summary>Демо-пользователи ролей operator/viewer (только при SeedRoleUsers=true).</summary>
    private static readonly (string Email, UserRole Role)[] RoleUsers =
    [
        ("operator@demo.local", UserRole.Operator),
        ("viewer@demo.local", UserRole.Viewer)
    ];

    private async Task<int> EnsureRoleUsersAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (!options.SeedRoleUsers)
        {
            return 0;
        }

        var created = 0;

        foreach (var (roleEmail, role) in RoleUsers)
        {
            var email = roleEmail.ToLowerInvariant();
            var exists = await context.Users
                .AnyAsync(
                    candidate => candidate.TenantId == tenantId && candidate.Email == email,
                    cancellationToken)
                .ConfigureAwait(false);

            if (exists)
            {
                continue;
            }

            context.Users.Add(new User
            {
                TenantId = tenantId,
                Email = email,
                PasswordHash = passwordHasher.Hash(options.AdminPassword!),
                Role = role.ToDbValue(),
                AuthMeta = "{}"
            });

            created++;
        }

        return created;
    }

    private void ValidateOptions()
    {
        if (string.IsNullOrWhiteSpace(options.AdminPassword))
        {
            throw new InvalidOperationException(
                $"Seed requires the {SeedOptions.PasswordEnvironmentVariable} environment variable: " +
                "admin password is never stored in configuration files.");
        }

        if (string.IsNullOrWhiteSpace(options.TenantName))
        {
            throw new InvalidOperationException("Seed tenant name must not be empty.");
        }

        if (!EnumTokens.TryParseDbValue<TenantPlan>(options.TenantPlan, out _))
        {
            throw new InvalidOperationException(
                $"Seed tenant plan '{options.TenantPlan}' is invalid. Allowed: pilot, basic, unlimited.");
        }

        if (string.IsNullOrWhiteSpace(options.AdminEmail) || !options.AdminEmail.Contains('@', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Seed admin email '{options.AdminEmail}' is invalid.");
        }
    }
}

/// <summary>Результат проверочного seed.</summary>
/// <param name="TenantId">Идентификатор тенанта.</param>
/// <param name="UserId">Идентификатор admin-пользователя.</param>
/// <param name="TenantCreated">true, если тенант был создан этим запуском.</param>
/// <param name="UserCreated">true, если пользователь был создан этим запуском.</param>
public sealed record SeedResult(Guid TenantId, Guid UserId, bool TenantCreated, bool UserCreated);
