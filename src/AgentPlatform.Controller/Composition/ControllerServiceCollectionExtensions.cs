using AgentPlatform.Controller.Agents;
using AgentPlatform.Controller.Connectors;
using AgentPlatform.Controller.Dialogs;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Repositories;
using AgentPlatform.Infrastructure.Security;
using AgentPlatform.LlmGateway;
using AgentPlatform.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentPlatform.Controller.Composition;

/// <summary>
/// Сборка Controller API: CRUD кабинета, Secret Service, Runtime для диалогов
/// и MCP-транспорт к Connector Host. Runtime использует те же порты, что Gateway,
/// но с демонстрационным LLM-клиентом (TODO: реальный LLM Gateway).
/// </summary>
public static class ControllerServiceCollectionExtensions
{
    /// <summary>Добавляет сервисы Controller API.</summary>
    public static IServiceCollection AddControllerServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.Configure<ConnectorHostOptions>(configuration.GetSection("ConnectorHost"));

        // Реестр коннекторов: импорт манифеста EchoTools из Connector Host.
        services.AddHttpClient(ConnectorRegistrySyncService.HttpClientName, client =>
            client.Timeout = TimeSpan.FromSeconds(10));
        services.AddScoped<ConnectorRegistrySyncService>();
        services.AddHostedService<ConnectorRegistryInitializer>();

        // Secret Service: AES-256 + KMS-заглушка (TODO(kms) в AesKmsSecretService).
        services.AddScoped<ISecretService, AesKmsSecretService>();

        // Порты Runtime (EF Core / PostgreSQL).
        services.AddScoped<IAgentConfigStore>(serviceProvider =>
            new EfAgentConfigStore(serviceProvider.GetRequiredService<AgentPlatformDbContext>()));
        services.AddScoped<ICheckpointStore>(serviceProvider =>
            new EfCheckpointStore(serviceProvider.GetRequiredService<AgentPlatformDbContext>()));
        services.AddScoped<IApprovalStore>(serviceProvider =>
            new EfApprovalStore(serviceProvider.GetRequiredService<AgentPlatformDbContext>()));

        // Тулы вызываются только через Connector Host (MCP, streamable HTTP).
        services.AddScoped<IToolInvoker, McpToolInvoker>();

        // Демонстрационный клиент модели: детерминированные вызовы echo_read/echo_write.
        // TODO(2.x) contract: реальный LLM Gateway (провайдер, ключи, квоты).
        services.AddSingleton<IChatClientFactory, DemoChatClientFactory>();

        services.AddMafAgentRuntime(new AgentRuntimeOptions
        {
            RedisConnectionString = configuration["Redis:ConnectionString"]
        });

        services.AddScoped<AgentDefinitionService>();
        services.AddScoped<ConnectorService>();
        services.AddScoped<CabinetDialogService>();

        return services;
    }
}
