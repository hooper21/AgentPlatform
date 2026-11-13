using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Dto;
using AgentPlatform.Runtime.Knowledge;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPlatform.Runtime;

/// <summary>
/// Регистрация RAG-порта (Knowledge Service) в хостах Gateway/Controller.
/// Порт вызывается Runtime перед LLM-ходом; Knowledge Service не зависит от
/// коннекторов и MCP.
/// </summary>
public static class KnowledgeRagServiceCollectionExtensions
{
    /// <summary>
    /// Подключает HTTP-адаптер Knowledge Service. Пустой baseUrl — RAG выключен
    /// (NullKnowledgeContextProvider из AddMafAgentRuntime).
    /// </summary>
    public static IServiceCollection AddPlatformKnowledgeRag(
        this IServiceCollection services,
        string? baseUrl,
        int topK = KnowledgeContext.DefaultTopK,
        double threshold = KnowledgeContext.DefaultThreshold,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return services;
        }

        var baseAddress = new Uri(baseUrl, UriKind.Absolute);
        var httpTimeout = timeout ?? TimeSpan.FromSeconds(3);

        services.AddSingleton<IKnowledgeContextProvider>(_ => new HttpKnowledgeContextProvider(
            new HttpClient { BaseAddress = baseAddress, Timeout = httpTimeout },
            topK,
            threshold));

        return services;
    }
}
