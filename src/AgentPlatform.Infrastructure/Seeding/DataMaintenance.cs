using AgentPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPlatform.Infrastructure.Seeding;

/// <summary>
/// CLI-команды обслуживания БД: <c>--migrate</c> и <c>--seed</c>.
/// Вызываются из сервисов до старта host-а, поэтому работают и в docker.
/// </summary>
public static class DataMaintenance
{
    public const string MigrateCommand = "--migrate";
    public const string SeedCommand = "--seed";

    /// <summary>true, если в аргументах есть хотя бы одна команда обслуживания.</summary>
    public static bool IsRequested(string[] args)
        => args.Any(argument => IsCommand(argument, MigrateCommand) || IsCommand(argument, SeedCommand));

    /// <summary>
    /// Выполняет запрошенные команды. Возвращает true, если сервис должен
    /// завершиться после их выполнения.
    /// </summary>
    public static async Task<bool> TryRunAsync(
        IServiceProvider services,
        string[] args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(args);

        var migrate = args.Any(argument => IsCommand(argument, MigrateCommand));
        var seed = args.Any(argument => IsCommand(argument, SeedCommand));
        if (!migrate && !seed)
        {
            return false;
        }

        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AgentPlatformDbContext>();

        if (migrate)
        {
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            Console.WriteLine("migrate: database is up to date");
        }

        if (seed)
        {
            var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
            var result = await seeder.SeedAsync(cancellationToken).ConfigureAwait(false);
            Console.WriteLine(
                $"seed: tenant={result.TenantId} (created={result.TenantCreated}), " +
                $"admin={result.UserId} (created={result.UserCreated})");
        }

        return true;
    }

    private static bool IsCommand(string argument, string command)
        => string.Equals(argument, command, StringComparison.OrdinalIgnoreCase);
}
