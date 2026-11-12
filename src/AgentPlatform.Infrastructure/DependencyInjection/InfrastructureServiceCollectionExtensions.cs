using AgentPlatform.Core.Abstractions;
using AgentPlatform.Infrastructure.Configuration;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Persistence.Interceptors;
using AgentPlatform.Infrastructure.Repositories;
using AgentPlatform.Infrastructure.Seeding;
using AgentPlatform.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPlatform.Infrastructure.DependencyInjection;

/// <summary>Регистрация инфраструктурного слоя (EF Core, Npgsql, репозитории, seed).</summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Добавляет DbContext (PostgreSQL), ITenantContext, tenant-фильтрованные
    /// и глобальные репозитории, интерсептор меток времени и seeder.
    /// </summary>
    public static IServiceCollection AddPlatformInfrastructure(
        this IServiceCollection services,
        PlatformInfrastructureOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new InvalidOperationException(
                "PostgreSQL connection string is not configured. " +
                $"Set the {ConnectionStringProvider.EnvironmentVariable} environment variable.");
        }

        services.AddSingleton(options.Seed);
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<AuditingInterceptor>();

        services.AddDbContext<AgentPlatformDbContext>((serviceProvider, builder) =>
        {
            builder.UseNpgsql(options.ConnectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(AgentPlatformDbContext).Assembly.FullName);
                npgsql.EnableRetryOnFailure(3);
            });

            if (options.EnableDetailedErrors)
            {
                builder.EnableDetailedErrors();
            }

            builder.AddInterceptors(serviceProvider.GetRequiredService<AuditingInterceptor>());
        });

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(serviceProvider => serviceProvider.GetRequiredService<TenantContext>());

        services.AddScoped(typeof(ITenantScopedRepository<>), typeof(TenantScopedRepository<>));
        services.AddScoped(typeof(IGlobalRepository<>), typeof(GlobalRepository<>));
        services.AddScoped<IScheduledRunStore, EfScheduledRunStore>();

        services.AddScoped<DatabaseSeeder>();

        return services;
    }
}
