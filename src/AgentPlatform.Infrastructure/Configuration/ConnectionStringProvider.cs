namespace AgentPlatform.Infrastructure.Configuration;

/// <summary>
/// Единственная точка получения строки подключения к PostgreSQL.
/// Порядок: env ConnectionStrings__Postgres → dev-фолбэк на локальный docker compose.
/// В файлах конфигурации строка подключения не хранится.
/// </summary>
public static class ConnectionStringProvider
{
    /// <summary>Переменная окружения со строкой подключения.</summary>
    public const string EnvironmentVariable = "ConnectionStrings__Postgres";

    /// <summary>Возвращает строку подключения.</summary>
    public static string Resolve()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return BuildDevelopmentFallback();
    }

    /// <summary>true, если строка подключения задана явно (env).</summary>
    public static bool IsExplicitlyConfigured()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>
    /// Фолбэк для локального запуска без переменных окружения: параметры совпадают
    /// с дефолтами docker-compose.yml (только localhost, не для production).
    /// </summary>
    private static string BuildDevelopmentFallback()
    {
        var host = Read("POSTGRES_HOST", "localhost");
        var port = Read("POSTGRES_PORT", "5432");
        var database = Read("POSTGRES_DB", "agentplatform");
        var username = Read("POSTGRES_USER", "agentplatform");
        var password = Read("POSTGRES_PASSWORD", "agentplatform");

        return $"Host={host};Port={port};Database={database};Username={username};Password={password};Include Error Detail=true";
    }

    private static string Read(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
