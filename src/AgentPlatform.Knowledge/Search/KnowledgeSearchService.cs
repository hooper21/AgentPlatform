using AgentPlatform.Core.Dto;
using AgentPlatform.Knowledge.Configuration;
using AgentPlatform.Knowledge.Embeddings;
using AgentPlatform.Knowledge.Misses;
using AgentPlatform.Knowledge.Search;
using Microsoft.Extensions.Logging;

namespace AgentPlatform.Knowledge.Search;

/// <summary>
/// RAG-поиск: эмбеддинг запроса → pgvector (cosine) → фильтр tenant_id.
/// Пустой результат — пустой список и запись в rag_misses (не исключение).
/// </summary>
public sealed class KnowledgeSearchService(
    IKnowledgeEmbedder embedder,
    IKnowledgeVectorStore vectorStore,
    IRagMissStore missStore,
    KnowledgeOptions options,
    TimeProvider timeProvider,
    ILogger<KnowledgeSearchService> logger)
{
    /// <summary>Максимальный topK (защита от выгрузки всей базы знаний).</summary>
    public const int MaxTopK = 20;

    private readonly IKnowledgeEmbedder _embedder =
        embedder ?? throw new ArgumentNullException(nameof(embedder));

    private readonly IKnowledgeVectorStore _vectorStore =
        vectorStore ?? throw new ArgumentNullException(nameof(vectorStore));

    private readonly IRagMissStore _missStore = missStore ?? throw new ArgumentNullException(nameof(missStore));

    private readonly KnowledgeOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<KnowledgeSearchService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Выполняет поиск. query обязателен; topK/threshold нормализуются.</summary>
    public async Task<IReadOnlyList<KnowledgeHit>> SearchAsync(
        Guid tenantId,
        string? query,
        int? topK,
        double? threshold,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("tenant_id is required for search", nameof(tenantId));
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("query is required for knowledge search", nameof(query));
        }

        var normalizedTopK = Math.Clamp(topK ?? _options.DefaultTopK, 1, MaxTopK);
        var normalizedThreshold = Math.Clamp(threshold ?? _options.DefaultThreshold, 0, 1);
        var embedding = await _embedder.EmbedAsync(tenantId, query.Trim(), cancellationToken).ConfigureAwait(false);

        var hits = await _vectorStore
            .SearchAsync(tenantId, embedding, normalizedTopK, normalizedThreshold, cancellationToken)
            .ConfigureAwait(false);

        if (hits.Count == 0)
        {
            _logger.LogInformation(
                "RAG miss: tenant {TenantId}, query '{Query}' returned no chunks above {Threshold}",
                tenantId,
                query,
                normalizedThreshold);

            await _missStore
                .LogAsync(tenantId, query.Trim(), _timeProvider.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
        }

        return hits;
    }
}
