using System.Diagnostics;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Json;
using AgentPlatform.Core.Scheduling;
using AgentPlatform.Infrastructure.Persistence.Entities;
using AgentPlatform.Knowledge.Chunking;
using AgentPlatform.Knowledge.Configuration;
using AgentPlatform.Knowledge.Contracts;
using AgentPlatform.Knowledge.Embeddings;
using AgentPlatform.Knowledge.Search;
using Microsoft.Extensions.Logging;

namespace AgentPlatform.Knowledge.Ingest;

/// <summary>
/// Инжест знаний: создание источника → pending → indexing → ready | error.
/// Ошибки пишутся в index_meta, источник не удаляется; переиндексация ручная
/// (<see cref="ReindexAsync"/>) или по расписанию (ReindexCron в index_meta).
/// Все операции — через TenantScopedRepository (tenant_id из ITenantContext).
/// </summary>
public sealed class KnowledgeIngestService(
    ITenantContext tenantContext,
    ITenantScopedRepository<KnowledgeSource> sources,
    IEnumerable<IKnowledgeProvider> providers,
    IKnowledgeEmbedder embedder,
    IKnowledgeVectorStore vectorStore,
    KnowledgeOptions options,
    TimeProvider timeProvider,
    ILogger<KnowledgeIngestService> logger)
{
    private readonly ITenantContext _tenantContext =
        tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));

    private readonly ITenantScopedRepository<KnowledgeSource> _sources =
        sources ?? throw new ArgumentNullException(nameof(sources));

    private readonly IReadOnlyList<IKnowledgeProvider> _providers =
        (providers ?? throw new ArgumentNullException(nameof(providers))).ToList();

    private readonly IKnowledgeEmbedder _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));

    private readonly IKnowledgeVectorStore _vectorStore =
        vectorStore ?? throw new ArgumentNullException(nameof(vectorStore));

    private readonly KnowledgeOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<KnowledgeIngestService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Создаёт источник и сразу запускает индексацию (MVP: inline; очередь
    /// фоновой индексации — отдельная задача). Ошибка инжеста не бросается:
    /// статус error и текст ошибки остаются в index_meta.
    /// </summary>
    public async Task<KnowledgeSource> CreateAsync(
        CreateKnowledgeSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var type = ParseType(request.Type);
        var title = RequireTitle(request.Title);
        var (reference, content) = ValidateReference(type, request.Ref, request.Content);
        var reindexCron = ValidateCron(request.ReindexCron);

        _tenantContext.RequireTenantId();

        var meta = new KnowledgeIndexMeta
        {
            ReindexCron = reindexCron,
            ManualContent = content
        };

        var source = new KnowledgeSource
        {
            Type = type.ToDbValue(),
            Ref = reference,
            Title = title,
            ChunkCount = 0,
            IndexStatus = KnowledgeIndexStatus.Pending.ToDbValue(),
            IndexMeta = meta.ToJson()
        };

        await _sources.AddAsync(source, cancellationToken).ConfigureAwait(false);
        await _sources.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await IndexAsync(source, content, cancellationToken).ConfigureAwait(false);
        return source;
    }

    /// <summary>Ручная переиндексация источника текущего тенанта (null — не найден).</summary>
    public async Task<KnowledgeSource?> ReindexAsync(
        Guid sourceId,
        CancellationToken cancellationToken = default)
    {
        var source = await _sources.FindAsync(sourceId, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        var meta = KnowledgeIndexMeta.Parse(source.IndexMeta);
        await IndexAsync(source, meta.ManualContent, cancellationToken).ConfigureAwait(false);
        return source;
    }

    /// <summary>
    /// Прогон инжеста: провайдер → чанки → эмбеддинги → замена чанков в хранилище.
    /// Любая ошибка (кроме отмены) фиксируется в index_meta; исключение не пробрасывается.
    /// </summary>
    private async Task IndexAsync(
        KnowledgeSource source,
        string? manualContent,
        CancellationToken cancellationToken)
    {
        var tenantId = _tenantContext.RequireTenantId();
        var type = EnumTokens.ParseDbValue<KnowledgeSourceType>(source.Type);
        var meta = KnowledgeIndexMeta.Parse(source.IndexMeta);
        var started = _timeProvider.GetTimestamp();
        var startedAt = _timeProvider.GetUtcNow();

        source.IndexStatus = KnowledgeIndexStatus.Indexing.ToDbValue();
        _sources.Update(source);
        await _sources.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var expectedDimensions = _options.EmbeddingDimensions;

            // Сверка размерности эмбеддера с knowledge_chunks.embedding (vector(768)
            // для nomic-embed-text). Несовпадение — исключение, данные не подгоняются.
            EnsureEmbeddingDimensions(
                _embedder.Dimensions,
                expectedDimensions,
                "knowledge_chunks.embedding");

            EnsureEmbeddingDimensions(
                await _vectorStore.GetEmbeddingDimensionsAsync(cancellationToken).ConfigureAwait(false),
                expectedDimensions,
                "knowledge_chunks.embedding");

            var provider = _providers.FirstOrDefault(candidate => candidate.SourceType == type)
                ?? throw new InvalidOperationException(
                    $"Провайдер знаний для типа '{source.Type}' не зарегистрирован.");

            var request = new KnowledgeProviderRequest(
                source.Title,
                string.IsNullOrWhiteSpace(source.Ref) ? null : source.Ref,
                manualContent ?? meta.ManualContent,
                meta.Cursor);

            var chunks = new List<KnowledgeChunkEmbedding>();
            var cursor = meta.Cursor;

            await foreach (var page in provider.ReadAsync(request, cancellationToken).ConfigureAwait(false))
            {
                cursor = page.Cursor ?? cursor;

                var drafts = TokenChunker.Split(
                    page.Content,
                    page.Heading,
                    _options.ChunkSizeTokens,
                    _options.ChunkOverlapTokens);

                foreach (var draft in drafts)
                {
                    var embedding = await _embedder
                        .EmbedAsync(tenantId, draft.Content, cancellationToken)
                        .ConfigureAwait(false);

                    EnsureEmbeddingDimensions(
                        embedding.Length,
                        expectedDimensions,
                        "knowledge_chunks.embedding");

                    chunks.Add(new KnowledgeChunkEmbedding(
                        draft.Content,
                        PlatformJson.SerializeDb(new KnowledgeChunkMetadata(
                            source.Title,
                            source.Type,
                            draft.Heading,
                            chunks.Count,
                            draft.TokenCount,
                            page.Cursor)),
                        embedding.ToArray()));
                }
            }

            if (chunks.Count == 0)
            {
                throw new InvalidOperationException(
                    "Источник не дал ни одного чанка: проверьте контент/доступность документа.");
            }

            await _vectorStore
                .ReplaceSourceChunksAsync(
                    new KnowledgeSourceRef(tenantId, source.Id, source.Title, source.Type),
                    chunks,
                    cancellationToken)
                .ConfigureAwait(false);

            source.ChunkCount = chunks.Count;
            source.IndexStatus = KnowledgeIndexStatus.Ready.ToDbValue();

            meta.Cursor = cursor;
            meta.ManualContent = manualContent ?? meta.ManualContent;
            meta.LastIndexedAt = _timeProvider.GetUtcNow();
            meta.AttemptedAt = startedAt;
            meta.DurationMs = (long)_timeProvider.GetElapsedTime(started).TotalMilliseconds;
            meta.Chunks = chunks.Count;
            meta.Dimensions = expectedDimensions;
            meta.Error = null;
            meta.ErrorType = null;

            _logger.LogInformation(
                "Knowledge source {SourceId} ({Type}) indexed: {Chunks} chunks",
                source.Id,
                source.Type,
                chunks.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            source.IndexStatus = KnowledgeIndexStatus.Error.ToDbValue();
            meta.Error = "Индексация отменена.";
            meta.ErrorType = nameof(OperationCanceledException);
            source.IndexMeta = meta.ToJson();
            _sources.Update(source);
            await _sources.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            source.IndexStatus = KnowledgeIndexStatus.Error.ToDbValue();
            meta.AttemptedAt = startedAt;
            meta.DurationMs = (long)_timeProvider.GetElapsedTime(started).TotalMilliseconds;
            meta.Error = exception.Message;
            meta.ErrorType = exception.GetType().Name;

            source.ChunkCount = await CountExistingChunksAsync(tenantId, source.Id, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogWarning(
                exception,
                "Knowledge source {SourceId} ({Type}) indexing failed: {Error}",
                source.Id,
                source.Type,
                exception.Message);
        }
        finally
        {
            source.IndexMeta = meta.ToJson();
            _sources.Update(source);
            await _sources.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static void EnsureEmbeddingDimensions(int? actual, int? expected, string? origin = null)
    {
        if (actual is null || expected is null || actual == expected.Value)
        {
            return;
        }

        var target = origin is null ? "knowledge_chunks.embedding" : origin;
        throw new EmbeddingDimensionMismatchException(
            $"Размерность эмбеддинга {actual.Value} не совпадает с {target} vector({expected.Value}). " +
            $"Индексирование остановлено: векторы не обрезаются и не дополняются " +
            $"(модель '{KnowledgeOptions.SectionName}:EmbeddingModel').",
            actual.Value,
            expected.Value);
    }

    private async Task<int> CountExistingChunksAsync(
        Guid tenantId,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _vectorStore
                .CountAsync(tenantId, sourceId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Failed to read chunk count for source {SourceId}", sourceId);
            return 0;
        }
    }

    private static KnowledgeSourceType ParseType(string? token)
    {
        if (!EnumTokens.TryParseDbValue<KnowledgeSourceType>(token, out var type))
        {
            throw new ArgumentException(
                $"type must be one of document | site | faq, got '{token}'.",
                nameof(token));
        }

        return type;
    }

    private static string RequireTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("title is required for a knowledge source.", nameof(title));
        }

        var value = title.Trim();
        if (value.Length > 300)
        {
            throw new ArgumentException("title must be at most 300 characters.", nameof(title));
        }

        return value;
    }

    private static (string Reference, string? Content) ValidateReference(
        KnowledgeSourceType type,
        string? reference,
        string? content)
    {
        if (type == KnowledgeSourceType.Faq)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new ArgumentException(
                    "Для источника type=faq обязателен content в теле запроса.",
                    nameof(content));
            }

            // DDL фазы 1: ref NOT NULL, поэтому для faq хранится пустая строка (API отдаёт null).
            return (string.Empty, content.Trim());
        }

        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new ArgumentException(
                $"ref is required for a knowledge source of type '{type.ToDbValue()}'.",
                nameof(reference));
        }

        var value = reference.Trim();
        if (value.Length > 500)
        {
            throw new ArgumentException("ref must be at most 500 characters.", nameof(reference));
        }

        return (value, content);
    }

    private static string? ValidateCron(string? cron)
    {
        if (string.IsNullOrWhiteSpace(cron))
        {
            return null;
        }

        if (!CronSchedule.TryParse(cron, out _, out var error))
        {
            throw new ArgumentException($"reindexCron is invalid: {error}", nameof(cron));
        }

        return cron.Trim();
    }
}
