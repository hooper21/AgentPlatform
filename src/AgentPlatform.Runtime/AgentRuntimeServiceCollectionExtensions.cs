using AgentPlatform.Core.Abstractions;
using AgentPlatform.Runtime.Orchestration;
using AgentPlatform.Runtime.Policy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        // Таймаут гейта: 24 ч по умолчанию и переопределение по тенанту (из options).
        services.AddSingleton<ITenantGateTimeoutProvider, AgentRuntimeGateTimeoutProvider>();

        // RAG-порт: хост подменяет его HTTP-адаптером Knowledge Service
        // (AddPlatformKnowledgeRag); без Knowledge Service — пустой контекст.
        services.TryAddSingleton<IKnowledgeContextProvider, NullKnowledgeContextProvider>();

        // AgentFactory зависит от портов хоста (IAgentConfigStore — EF/DbContext, scoped),
        // поэтому его lifetime совпадает с lifetime оркестратора.
        services.AddScoped<AgentFactory>();
        services.AddScoped<IOrchestrationRuntime, MafOrchestrationRuntime>();

        return services;
    }
}
