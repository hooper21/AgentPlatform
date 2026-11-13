using System.Data.Common;
using System.Globalization;
using AgentPlatform.Core.Dto;
using AgentPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Knowledge.Search;

/// <summary>
/// pgvector-хранилище знаний: cosine distance (<c>&lt;=&gt;</c>), фильтр по tenant_id
/// в обоих запросах. Столбец embedding не входит в EF-модель (см. миграцию), поэтому
/// чтение/запись идут parameterized SQL: embedding передаётся литералом и CAST AS vector.
/// </summary>
public sealed class PgVectorKnowledgeVectorStore(AgentPlatformDbContext context) : IKnowledgeVectorStore
{
    private const string SearchSql =
        """
        SELECT c.id,
               c.source_id,
               c.content,
               s.title,
               s.type,
               (1 - (c.embedding <=> CAST(@query AS vector)))::double precision AS score
        FROM knowledge_chunks c
        JOIN knowledge_sources s
          ON s.id = c.source_id AND s.tenant_id = c.tenant_id
        WHERE c.tenant_id = @tenant_id
          AND c.embedding IS NOT NULL
          AND (1 - (c.embedding <=> CAST(@query AS vector))) >= @threshold
        ORDER BY c.embedding <=> CAST(@query AS vector)
        LIMIT @top_k;
        """;

    private const string DimensionSql =
        """
        SELECT atttypmod
        FROM pg_attribute
        WHERE attrelid = 'knowledge_chunks'::regclass
          AND attname = 'embedding'
          AND NOT attisdropped;
        """;

    private readonly AgentPlatformDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public async Task<int?> GetEmbeddingDimensionsAsync(CancellationToken cancellationToken = default)
    {
        await _context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var connection = _context.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = DimensionSql;

            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return value is null or DBNull
                ? null
                : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        finally
        {
            await _context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(
        Guid tenantId,
        Guid sourceId,
        CancellationToken cancellationToken = default)
        => await _context.KnowledgeChunks
            .AsNoTracking()
            .CountAsync(
                chunk => chunk.TenantId == tenantId && chunk.SourceId == sourceId,
                cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task ReplaceSourceChunksAsync(
        KnowledgeSourceRef source,
        IReadOnlyList<KnowledgeChunkEmbedding> chunks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(chunks);

        await using var transaction = await _context.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await _context.Database
            .ExecuteSqlRawAsync(
                "DELETE FROM knowledge_chunks WHERE tenant_id = {0} AND source_id = {1};",
                [source.TenantId, source.SourceId],
                cancellationToken)
            .ConfigureAwait(false);

        await InsertChunksAsync(source, chunks, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReplacePageChunksAsync(
        KnowledgeSourceRef source,
        string externalId,
        IReadOnlyList<KnowledgeChunkEmbedding> chunks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        ArgumentNullException.ThrowIfNull(chunks);

        await using var transaction = await _context.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await _context.Database
            .ExecuteSqlRawAsync(
                """
                DELETE FROM knowledge_chunks
                WHERE tenant_id = {0}
                  AND source_id = {1}
                  AND metadata->>'external_id' = {2};
                """,
                [source.TenantId, source.SourceId, externalId],
                cancellationToken)
            .ConfigureAwait(false);

        await InsertChunksAsync(source, chunks, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeletePageChunksAsync(
        Guid tenantId,
        Guid sourceId,
        string externalId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);

        await _context.Database
            .ExecuteSqlRawAsync(
                """
                DELETE FROM knowledge_chunks
                WHERE tenant_id = {0}
                  AND source_id = {1}
                  AND metadata->>'external_id' = {2};
                """,
                [tenantId, sourceId, externalId],
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteSourceChunksAsync(
        Guid tenantId,
        Guid sourceId,
        CancellationToken cancellationToken = default)
        => await _context.Database
            .ExecuteSqlRawAsync(
                "DELETE FROM knowledge_chunks WHERE tenant_id = {0} AND source_id = {1};",
                [tenantId, sourceId],
                cancellationToken)
            .ConfigureAwait(false);

    private async Task InsertChunksAsync(
        KnowledgeSourceRef source,
        IReadOnlyList<KnowledgeChunkEmbedding> chunks,
        CancellationToken cancellationToken)
    {
        foreach (var chunk in chunks)
        {
            ArgumentNullException.ThrowIfNull(chunk);

            await _context.Database
                .ExecuteSqlRawAsync(
                    """
                    INSERT INTO knowledge_chunks (tenant_id, source_id, content, metadata, created_at, embedding)
                    VALUES ({0}, {1}, {2}, CAST({3} AS jsonb), {4}, CAST({5} AS vector));
                    """,
                    [
                        source.TenantId,
                        source.SourceId,
                        chunk.Content,
                        chunk.Metadata,
                        DateTimeOffset.UtcNow,
                        VectorLiteral.Format(chunk.Embedding)
                    ],
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<KnowledgeHit>> SearchAsync(
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

        if (queryEmbedding.IsEmpty)
        {
            return [];
        }

        await _context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var connection = _context.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = SearchSql;

            AddParameter(command, "tenant_id", tenantId);
            AddParameter(command, "query", VectorLiteral.Format(queryEmbedding.Span));
            AddParameter(command, "threshold", threshold);
            AddParameter(command, "top_k", Math.Max(1, topK));

            var hits = new List<KnowledgeHit>();
            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                hits.Add(new KnowledgeHit(
                    reader.GetGuid(1),
                    reader.GetInt64(0),
                    reader.GetString(2),
                    reader.GetDouble(5),
                    reader.GetString(3),
                    reader.GetString(4)));
            }

            return hits;
        }
        finally
        {
            await _context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
