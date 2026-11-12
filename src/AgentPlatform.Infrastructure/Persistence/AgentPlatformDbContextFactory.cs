using AgentPlatform.Infrastructure.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AgentPlatform.Infrastructure.Persistence;

/// <summary>
/// Фабрика для design-time команд EF (`dotnet ef migrations add`, `dotnet ef database update`).
/// Строка подключения берётся из env ConnectionStrings__Postgres либо из
/// dev-фолбэка на локальный docker compose.
/// </summary>
public sealed class AgentPlatformDbContextFactory : IDesignTimeDbContextFactory<AgentPlatformDbContext>
{
    public AgentPlatformDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AgentPlatformDbContext>()
            .UseNpgsql(
                ConnectionStringProvider.Resolve(),
                npgsql => npgsql.MigrationsAssembly(typeof(AgentPlatformDbContext).Assembly.FullName))
            .Options;

        return new AgentPlatformDbContext(options);
    }
}
