namespace AgentPlatform.Infrastructure.Configuration;

/// <summary>
/// Настройки инфраструктурного слоя. Секреты и строки подключения приходят
/// ТОЛЬКО из переменных окружения (см. README), в appsettings их нет.
/// </summary>
public sealed class PlatformInfrastructureOptions
{
    /// <summary>Строка подключения к PostgreSQL (env: ConnectionStrings__Postgres).</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Параметры проверочного seed.</summary>
    public SeedOptions Seed { get; set; } = new();

    /// <summary>Включать подробности ошибок Npgsql (только для локальной разработки).</summary>
    public bool EnableDetailedErrors { get; set; }

    /// <summary>Собирает настройки из переменных окружения (значения по умолчанию — для docker compose).</summary>
    public static PlatformInfrastructureOptions FromEnvironment()
    {
        var options = new PlatformInfrastructureOptions
        {
            ConnectionString = ConnectionStringProvider.Resolve(),
            Seed = SeedOptions.FromEnvironment()
        };

        options.EnableDetailedErrors = IsTruthy(Environment.GetEnvironmentVariable("AGENTPLATFORM_DB_DETAILED_ERRORS"));
        return options;
    }

    internal static bool IsTruthy(string? value)
        => bool.TryParse(value, out var parsed) && parsed;
}

/// <summary>Параметры проверочного seed (тенант + admin).</summary>
public sealed class SeedOptions
{
    public const string PasswordEnvironmentVariable = "AGENTPLATFORM_SEED_ADMIN_PASSWORD";
    public const string EmailEnvironmentVariable = "AGENTPLATFORM_SEED_ADMIN_EMAIL";
    public const string TenantNameEnvironmentVariable = "AGENTPLATFORM_SEED_TENANT_NAME";

    /// <summary>Имя тестового тенанта.</summary>
    public string TenantName { get; set; } = "Demo Tenant";

    /// <summary>План тестового тенанта: pilot | basic | unlimited.</summary>
    public string TenantPlan { get; set; } = "pilot";

    /// <summary>Email администратора.</summary>
    public string AdminEmail { get; set; } = "admin@demo.local";

    /// <summary>Пароль администратора (только из env, в файлах не хранится).</summary>
    public string? AdminPassword { get; set; }

    /// <summary>Собирает параметры seed из окружения.</summary>
    public static SeedOptions FromEnvironment()
    {
        var options = new SeedOptions
        {
            AdminPassword = Environment.GetEnvironmentVariable(PasswordEnvironmentVariable)
        };

        var email = Environment.GetEnvironmentVariable(EmailEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(email))
        {
            options.AdminEmail = email;
        }

        var tenantName = Environment.GetEnvironmentVariable(TenantNameEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(tenantName))
        {
            options.TenantName = tenantName;
        }

        return options;
    }
}
