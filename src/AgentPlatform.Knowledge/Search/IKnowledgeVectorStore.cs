using AgentPlatform.Core.Dto;

namespace AgentPlatform.Knowledge.Search;

/// <summary>Чанк с вектором для записи в хранилище знаний.</summary>
/// <param name="Content">Текст чанка.</param>
/// <param name="Metadata">jsonb-метаданные (заголовок, источник, позиция).</param>
/// <param name="Embedding">Вектор эмбеддинга (размерность проверена до записи).</param>
/// <param name="ExternalId">Идентификатор страницы источника (metadata.external_id).</param>
public sealed record KnowledgeChunkEmbedding(
    string Content,
    string Metadata,
    float[] Embedding,
    string? ExternalId = null);

/// <summary>Идентификация источника при замене чанков.</summary>
/// <param name="TenantId">Тенант-владелец.</param>
/// <param name="SourceId">Источник знаний.</param>
/// <param name="Title">Заголовок источника (для блока RAG).</param>
/// <param name="SourceType">document | site | faq.</param>
public sealed record KnowledgeSourceRef(Guid TenantId, Guid SourceId, string Title, string SourceType);

/// <summary>
/// Векторное хранилище знаний. Production-реализация — pgvector (cosine distance),
/// тестовая — in-memory. Все операции строго tenant-scoped.
/// </summary>
public interface IKnowledgeVectorStore
{
    /// <summary>
    /// Фактическая размерность knowledge_chunks.embedding (null — хранилище без схемы,
    /// например in-memory). Используется для сверки с эмбеддером до инжеста.
    /// </summary>
    Task<int?> GetEmbeddingDimensionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Число чанков источника текущего тенанта.</summary>
    Task<int> CountAsync(Guid tenantId, Guid sourceId, CancellationToken cancellationToken = default);

    /// <summary>Заменяет все чанки источника (переиндексация): delete + insert.</summary>
    Task ReplaceSourceChunksAsync(
        KnowledgeSourceRef source,
        IReadOnlyList<KnowledgeChunkEmbedding> chunks,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Заменяет чанки одной страницы источника (инкрементальная синхронизация):
    /// удаляет прежние чанки по external_id и вставляет новые.
    /// </summary>
    Task ReplacePageChunksAsync(
        KnowledgeSourceRef source,
        string externalId,
        IReadOnlyList<KnowledgeChunkEmbedding> chunks,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Удаляет чанки страницы источника по external_id (tombstone: isDeleted=true).
    /// </summary>
    Task DeletePageChunksAsync(
        Guid tenantId,
        Guid sourceId,
        string externalId,
        CancellationToken cancellationToken = default);

    /// <summary>Удаляет чанки источника (служебная операция инжеста).</summary>
    Task DeleteSourceChunksAsync(Guid tenantId, Guid sourceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Поиск по косинусному сходству с фильтром по tenant_id.
    /// Пустой результат — пустой список (не исключение).
    /// </summary>
    Task<IReadOnlyList<KnowledgeHit>> SearchAsync(
        Guid tenantId,
        ReadOnlyMemory<float> queryEmbedding,
        int topK,
        double threshold,
        CancellationToken cancellationToken = default);
}
