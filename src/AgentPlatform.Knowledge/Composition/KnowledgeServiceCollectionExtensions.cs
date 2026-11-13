using AgentPlatform.Core.Abstractions;
using AgentPlatform.Knowledge.Configuration;
using AgentPlatform.Knowledge.Embeddings;
using AgentPlatform.Knowledge.Ingest;
using AgentPlatform.Knowledge.Ingest.Providers;
using AgentPlatform.Knowledge.Misses;
using AgentPlatform.Knowledge.Reindex;
using AgentPlatform.Knowledge.Search;
using AgentPlatform.LlmGateway;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPlatform.Knowledge.Composition;

/// <summary>Сборка Knowledge Service в DI: инжест, эмбеддер, pgvector, API, переиндексация.</summary>
public static class KnowledgeServiceCollectionExtensions
{
    /// <summary>Добавляет сервисы знаний. Требует AddPlatformInfrastructure (DbContext, tenant-контекст).</summary>
    public static IServiceCollection AddKnowledgeService(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new KnowledgeOptions();
        configuration.GetSection(KnowledgeOptions.SectionName).Bind(options);
        options.Validate();

        services.AddSingleton(options);
        services.AddSingleton<TimeProvider>(TimeProvider.System);

        // Эмбеддинги: IChatClient из LLM Gateway, модель/endpoint — llm_config тенанта.
        // TODO(llm-gateway): реальная фабрика провайдера; заглушка стартует без ключей.
        services.AddSingleton<IChatClientFactory, StubChatClientFactory>();
        services.AddScoped<IKnowledgeEmbedder, TenantChatClientEmbedder>();

        // Инжест-провайдеры: document | site | faq — реализации контракта Core.
        // Новый тип источника добавляется регистрацией IKnowledgeProvider, без
        // изменения пайплайна и API.
        services.AddSingleton<IKnowledgeProvider, DocumentProvider>();
        services.AddSingleton<IKnowledgeProvider, SiteProvider>();
        services.AddSingleton<IKnowledgeProvider, FaqProvider>();
        services.AddSingleton<KnowledgeProviderRegistry>();

        if (options.UseInMemoryVectorStore)
        {
            services.AddSingleton<InMemoryKnowledgeVectorStore>();
            services.AddSingleton<IKnowledgeVectorStore>(provider =>
                provider.GetRequiredService<InMemoryKnowledgeVectorStore>());
            services.AddSingleton<InMemoryRagMissStore>();
            services.AddSingleton<IRagMissStore>(provider =>
                provider.GetRequiredService<InMemoryRagMissStore>());
        }
        else
        {
            services.AddScoped<IKnowledgeVectorStore, PgVectorKnowledgeVectorStore>();
            services.AddScoped<IRagMissStore, PgRagMissStore>();
        }

        services.AddScoped<KnowledgeIngestService>();
        services.AddScoped<KnowledgeSearchService>();

        services.AddHttpClient(SiteProvider.HttpClientName, client =>
        {
            // Таймаут запроса 5 с (плюс запас на чтение тела); реальный дедлайн
            // на каждый вызов задаётся CancellationTokenSource в провайдере.
            client.Timeout = options.SiteRequestTimeout + TimeSpan.FromSeconds(1);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.SiteUserAgent);
        });

        if (options.EnableScheduledReindex)
        {
            services.AddHostedService<KnowledgeReindexWorker>();
        }

        return services;
    }
}
