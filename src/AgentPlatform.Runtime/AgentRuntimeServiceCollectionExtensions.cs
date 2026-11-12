using AgentPlatform.Core.Abstractions;
using AgentPlatform.Runtime.Orchestration;
using AgentPlatform.Runtime.Policy;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPlatform.Runtime;

/// <summary>
/// Регистрация Runtime агентов в DI.
/// <para>
/// Хост (Gateway) дополнительно регистрирует адаптеры портов:
/// <see cref="IAgentConfigStore"/>, <see cref="ICheckpointStore"/> (Infrastructure),
/// <see cref="IToolInvoker"/> (Connector Host) и
/// <c>AgentPlatform.LlmGateway.IChatClientFactory</c> (LLM Gateway).
/// </para>
/// </summary>
public static class AgentRuntimeServiceCollectionExtensions
{
    /// <summary>Добавляет MafOrchestrationRuntime, AgentFactory, кеш конфигурации и Policy Engine.</summary>
    public static IServiceCollection AddMafAgentRuntime(
        this IServiceCollection services,
        AgentRuntimeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        options ??= new AgentRuntimeOptions();

        services.AddSingleton(options);
        services.AddSingleton<IAgentConfigCache>(_ => options.CreateConfigCache());

        // PolicyEngine — конкретная реализация Runtime; интерфейс отдаёт тот же объект.
        services.AddSingleton<PolicyEngine>();
        services.AddSingleton<IPolicyEngine>(serviceProvider => serviceProvider.GetRequiredService<PolicyEngine>());

        services.AddSingleton<AgentFactory>();
        services.AddScoped<IOrchestrationRuntime, MafOrchestrationRuntime>();

        return services;
    }
}
