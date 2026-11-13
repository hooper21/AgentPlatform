using AgentPlatform.Core.Dto;

namespace AgentPlatform.Knowledge.Search;

/// <summary>Снимок чанка in-memory хранилища (диагностика и тесты метаданных).</summary>
/// <param name="Id">Идентификатор чанка.</param>
/// <param name="Content">Текст чанка.</param>
/// <param name="Metadata">jsonb-метаданные.</param>
/// <param name="Embedding">Вектор.</param>
/// <param name="ExternalId">Идентификатор страницы источника.</param>
public sealed record KnowledgeChunkSnapshot(
    long Id,
    string Content,
    string Metadata,
    float[] Embedding,
    string? ExternalId = null);

/// <summary>
/// In-memory векторное хранилище: косинусное сходство в памяти процесса.
/// Используется в тестах и локальном запуске без pgvector; tenant-фильтр
/// применяется ко всем операциям так же, как в SQL-реализации.
/// </summary>
public sealed class InMemoryKnowledgeVectorStore : IKnowledgeVectorStore
{
    private sealed record StoredChunk(
        long Id,
        Guid TenantId,
        Guid SourceId,
        string Title,
        string SourceType,
        string Content,
        string Metadata,
        float[] Embedding,
        string? ExternalId);

    private readonly Dictionary<(Guid TenantId, Guid SourceId), (string Title, string SourceType, List<StoredChunk> Chunks)> _sources = [];
    private readonly object _gate = new();
    private long _nextId;

    /// <summary>Снимок чанков источника текущего тенанта (для проверок метаданных).</summary>
    public IReadOnlyList<KnowledgeChunkSnapshot> GetSourceChunks(Guid tenantId, Guid sourceId)
    {
        lock (_gate)
        {
            return _sources.TryGetValue((tenantId, sourceId), out var source)
                ? source.Chunks
                    .Select(chunk => new KnowledgeChunkSnapshot(
                        chunk.Id,
                        chunk.Content,
                        chunk.Metadata,
                        chunk.Embedding,
                        chunk.ExternalId))
                    .ToList()
                : [];
        }
    }

    /// <inheritdoc />
    public Task<int?> GetEmbeddingDimensionsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<int?>(null);

    /// <inheritdoc />
    public Task<int> CountAsync(Guid tenantId, Guid sourceId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(
                _sources.TryGetValue((tenantId, sourceId), out var source) ? source.Chunks.Count : 0);
        }
    }

    /// <inheritdoc />
    public Task ReplaceSourceChunksAsync(
        KnowledgeSourceRef source,
        IReadOnlyList<KnowledgeChunkEmbedding> chunks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(chunks);

        lock (_gate)
        {
            var stored = CreateStored(source, chunks);
            _sources[(source.TenantId, source.SourceId)] = (source.Title, source.SourceType, stored);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ReplacePageChunksAsync(
        KnowledgeSourceRef source,
        string externalId,
        IReadOnlyList<KnowledgeChunkEmbedding> chunks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        ArgumentNullException.ThrowIfNull(chunks);

        lock (_gate)
        {
            _sources.TryGetValue((source.TenantId, source.SourceId), out var existing);
            var stored = existing.Chunks is null
                ? []
                : existing.Chunks
                    .Where(chunk => !string.Equals(chunk.ExternalId, externalId, StringComparison.Ordinal))
                    .ToList();

            stored.AddRange(CreateStored(source, chunks));
            _sources[(source.TenantId, source.SourceId)] = (source.Title, source.SourceType, stored);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeletePageChunksAsync(
        Guid tenantId,
        Guid sourceId,
        string externalId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);

        lock (_gate)
        {
            if (!_sources.TryGetValue((tenantId, sourceId), out var source))
            {
                return Task.CompletedTask;
            }

            var remaining = source.Chunks
                .Where(chunk => !string.Equals(chunk.ExternalId, externalId, StringComparison.Ordinal))
                .ToList();

            _sources[(tenantId, sourceId)] = (source.Title, source.SourceType, remaining);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteSourceChunksAsync(Guid tenantId, Guid sourceId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _sources.Remove((tenantId, sourceId));
        }

        return Task.CompletedTask;
    }

    private List<StoredChunk> CreateStored(
        KnowledgeSourceRef source,
        IReadOnlyList<KnowledgeChunkEmbedding> chunks)
        => chunks
            .Select(chunk => new StoredChunk(
                ++_nextId,
                source.TenantId,
                source.SourceId,
                source.Title,
                source.SourceType,
                chunk.Content,
                chunk.Metadata,
                chunk.Embedding,
                chunk.ExternalId))
            .ToList();

    /// <inheritdoc />
    public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(
        Guid tenantId,
        ReadOnlyMemory<float> queryEmbedding,
        int topK,
        double threshold,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("tenant_id is required for search", nameof(tenantId));
        }

        var query = queryEmbedding.ToArray();
        List<KnowledgeHit> hits;

        lock (_gate)
        {
            hits = _sources
                .Where(entry => entry.Key.TenantId == tenantId)
                .SelectMany(entry => entry.Value.Chunks)
                .Select(chunk => new
                {
                    Chunk = chunk,
                    Score = CosineSimilarity(query, chunk.Embedding)
                })
                .Where(candidate => candidate.Score >= threshold)
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Chunk.Id)
                .Take(Math.Max(1, topK))
                .Select(candidate => new KnowledgeHit(
                    candidate.Chunk.SourceId,
                    candidate.Chunk.Id,
                    candidate.Chunk.Content,
                    candidate.Score,
                    candidate.Chunk.Title,
                    candidate.Chunk.SourceType))
                .ToList();
        }

        return Task.FromResult<IReadOnlyList<KnowledgeHit>>(hits);
    }

    private static double CosineSimilarity(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length || left.Length == 0)
        {
            return 0;
        }

        double dot = 0;
        double leftNorm = 0;
        double rightNorm = 0;

        for (var index = 0; index < left.Length; index++)
        {
            dot += left[index] * (double)right[index];
            leftNorm += left[index] * (double)left[index];
            rightNorm += right[index] * (double)right[index];
        }

        if (leftNorm == 0 || rightNorm == 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
    }
}
