using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Dto;
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
/// Универсальный инжест знаний: источник → провайдер (IKnowledgeProvider) →
/// чанки → эмбеддинги → pgvector. Пайплайн не знает типов источников: маршрутизация
/// идёт через <see cref="KnowledgeProviderRegistry"/>, а схема страницы и курсора
/// описана контрактом Core.
/// <para>
/// Полная синхронизация заменяет все чанки источника. Инкрементальная — применяет
/// изменения постранично: isDeleted=true удаляет чанки страницы по external_id,
/// изменённая страница заменяет только свои чанки. Ошибки пишутся в index_meta,
/// источник не удаляется. Все операции — через TenantScopedRepository
/// (tenant_id из ITenantContext).
/// </para>
/// </summary>
public sealed class KnowledgeIngestService(
    ITenantContext tenantContext,
    ITenantScopedRepository<KnowledgeSource> sources,
    KnowledgeProviderRegistry providers,
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

    private readonly KnowledgeProviderRegistry _providers =
        providers ?? throw new ArgumentNullException(nameof(providers));

    private readonly IKnowledgeEmbedder _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));

    private readonly IKnowledgeVectorStore _vectorStore =
        vectorStore ?? throw new ArgumentNullException(nameof(vectorStore));

    private readonly KnowledgeOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<KnowledgeIngestService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Создаёт источник и сразу запускает полную индексацию (MVP: inline; очередь
    /// фоновой индексации — отдельная задача). Ошибка инжеста не бросается:
    /// статус error и текст ошибки остаются в index_meta.
    /// </summary>
    public async Task<KnowledgeSource> CreateAsync(
        CreateKnowledgeSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var info = ResolveProviderInfo(request.Type);
        var title = RequireTitle(request.Title);
        var (location, content) = ValidateConnection(info, request.Ref, request.Content);
        var schedule = ValidateCron(request.SyncSchedule ?? request.ReindexCron);
        var config = new KnowledgeProviderConfig(
            Title: title,
            Location: location,
            Content: content,
            CredentialsRef: Normalize(request.CredentialsRef),
            Parameters: NormalizeParameters(request.Config));

        _tenantContext.RequireTenantId();

        var meta = new KnowledgeIndexMeta
        {
            ReindexCron = schedule,
            ManualContent = content
        };

        var source = new KnowledgeSource
        {
            Type = info.ProviderType,
            ProviderType = info.ProviderType,
            Ref = location ?? string.Empty,
            Config = KnowledgeProviderConfigJson.ToJson(config),
            CredentialsRef = config.CredentialsRef,
            Title = title,
            ChunkCount = 0,
            IndexStatus = KnowledgeIndexStatus.Pending.ToDbValue(),
            SyncSchedule = schedule,
            IndexMeta = meta.ToJson()
        };

        await _sources.AddAsync(source, cancellationToken).ConfigureAwait(false);
        await _sources.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await IndexAsync(source, meta, IndexMode.Full, cancellationToken).ConfigureAwait(false);
        return source;
    }

    /// <summary>Полная переиндексация источника текущего тенанта (null — не найден).</summary>
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
        await IndexAsync(source, meta, IndexMode.Full, cancellationToken).ConfigureAwait(false);
        return source;
    }

    /// <summary>
    /// Инкрементальная синхронизация источника текущего тенанта (null — не найден).
    /// Курсор берётся из knowledge_sources.last_cursor; пустой поток изменений
    /// оставляет курсор прежним.
    /// </summary>
    public async Task<KnowledgeSource?> SyncIncrementalAsync(
        Guid sourceId,
        CancellationToken cancellationToken = default)
    {
        var source = await _sources.FindAsync(sourceId, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        var meta = KnowledgeIndexMeta.Parse(source.IndexMeta);
        await IndexAsync(source, meta, IndexMode.Incremental, cancellationToken).ConfigureAwait(false);
        return source;
    }

    /// <summary>
    /// Синхронизация по расписанию/вебхуку: инкремент, если провайдер его
    /// поддерживает и курсор уже есть; иначе — полная переиндексация.
    /// </summary>
    public async Task<KnowledgeSource?> SyncAsync(
        Guid sourceId,
        CancellationToken cancellationToken = default)
    {
        var source = await _sources.FindAsync(sourceId, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        var meta = KnowledgeIndexMeta.Parse(source.IndexMeta);
        var info = _providers.Resolve(source).GetSourceInfo();
        var mode = info.SupportsIncremental && !string.IsNullOrWhiteSpace(source.LastCursor)
            ? IndexMode.Incremental
            : IndexMode.Full;

        await IndexAsync(source, meta, mode, cancellationToken).ConfigureAwait(false);
        return source;
    }

    private enum IndexMode
    {
        Full = 0,
        Incremental = 1
    }

    /// <summary>
    /// Прогон синхронизации: провайдер → чанки → эмбеддинги → хранилище.
    /// Любая ошибка (кроме отмены) фиксируется в index_meta; исключение не пробрасывается.
    /// </summary>
    private async Task IndexAsync(
        KnowledgeSource source,
        KnowledgeIndexMeta meta,
        IndexMode mode,
        CancellationToken cancellationToken)
    {
        var tenantId = _tenantContext.RequireTenantId();
        var provider = _providers.Resolve(source);
        var info = provider.GetSourceInfo();
        var config = KnowledgeProviderConfigJson.FromSource(source, meta);
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

            var effectiveMode = mode == IndexMode.Incremental && !info.SupportsIncremental
                ? IndexMode.Full
                : mode;

            var chunkCount = effectiveMode == IndexMode.Full
                ? await RunFullAsync(tenantId, source, provider, config, cancellationToken).ConfigureAwait(false)
                : await RunIncrementalAsync(tenantId, source, provider, config, cancellationToken)
                    .ConfigureAwait(false);

            source.ChunkCount = chunkCount;
            source.IndexStatus = KnowledgeIndexStatus.Ready.ToDbValue();

            meta.Cursor = source.LastCursor;
            meta.ManualContent = config.Content ?? meta.ManualContent;
            meta.LastIndexedAt = _timeProvider.GetUtcNow();
            meta.AttemptedAt = startedAt;
            meta.DurationMs = (long)_timeProvider.GetElapsedTime(started).TotalMilliseconds;
            meta.Chunks = chunkCount;
            meta.Dimensions = expectedDimensions;
            meta.Error = null;
            meta.ErrorType = null;

            _logger.LogInformation(
                "Knowledge source {SourceId} ({Type}/{Provider}) {Mode} sync: {Chunks} chunks, cursor {Cursor}",
                source.Id,
                source.Type,
                info.ProviderType,
                effectiveMode,
                chunkCount,
                source.LastCursor);
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
                "Knowledge source {SourceId} ({Type}/{Provider}) {Mode} sync failed: {Error}",
                source.Id,
                source.Type,
                info.ProviderType,
                mode,
                exception.Message);
        }
        finally
        {
            source.IndexMeta = meta.ToJson();
            _sources.Update(source);
            await _sources.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }



    private async Task<int> RunFullAsync(
        Guid tenantId,
        KnowledgeSource source,
        IKnowledgeProvider provider,
        KnowledgeProviderConfig config,
        CancellationToken cancellationToken)
    {
        var chunks = new List<KnowledgeChunkEmbedding>();
        var cursor = source.LastCursor;
        var position = 0;

        await foreach (var page in provider.FetchAll(config, since: null, cancellationToken).ConfigureAwait(false))
        {
            // Tombstone в полной выдаче не индексируется: полная замена и так
            // удаляет чанки, которых нет в источнике.
            if (page.IsDeleted)
            {
                continue;
            }

            var pageChunks = await BuildChunksAsync(
                tenantId,
                source,
                page,
                page.Cursor,
                position,
                cancellationToken).ConfigureAwait(false);

            chunks.AddRange(pageChunks);
            position += pageChunks.Count;
            cursor = page.Cursor ?? cursor;
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

        source.LastCursor = cursor;
        return chunks.Count;
    }

    private async Task<int> RunIncrementalAsync(
        Guid tenantId,
        KnowledgeSource source,
        IKnowledgeProvider provider,
        KnowledgeProviderConfig config,
        CancellationToken cancellationToken)
    {
        var sourceRef = new KnowledgeSourceRef(tenantId, source.Id, source.Title, source.Type);
        var cursor = source.LastCursor;
        var changed = 0;
        var deleted = 0;

        await foreach (var change in provider
            .FetchIncremental(config, cursor, cancellationToken)
            .ConfigureAwait(false))
        {
            var page = change.Page;
            cursor = change.Cursor ?? page.Cursor ?? cursor;

            if (page.IsDeleted)
            {
                await _vectorStore
                    .DeletePageChunksAsync(tenantId, source.Id, page.ExternalId, cancellationToken)
                    .ConfigureAwait(false);
                deleted++;
                continue;
            }

            var chunks = await BuildChunksAsync(
                tenantId,
                source,
                page,
                change.Cursor ?? page.Cursor,
                positionOffset: 0,
                cancellationToken).ConfigureAwait(false);

            if (chunks.Count == 0)
            {
                // Страница стала пустой — прежние чанки удаляем, чтобы поиск не
                // возвращал устаревший контент.
                await _vectorStore
                    .DeletePageChunksAsync(tenantId, source.Id, page.ExternalId, cancellationToken)
                    .ConfigureAwait(false);
                deleted++;
                continue;
            }

            await _vectorStore
                .ReplacePageChunksAsync(sourceRef, page.ExternalId, chunks, cancellationToken)
                .ConfigureAwait(false);
            changed++;
        }

        source.LastCursor = cursor;

        _logger.LogInformation(
            "Knowledge source {SourceId} incremental: {Changed} pages changed, {Deleted} deleted",
            source.Id,
            changed,
            deleted);

        return await CountExistingChunksAsync(tenantId, source.Id, cancellationToken).ConfigureAwait(false);
    }


    private async Task<IReadOnlyList<KnowledgeChunkEmbedding>> BuildChunksAsync(
        Guid tenantId,
        KnowledgeSource source,
        KnowledgePage page,
        string? cursor,
        int positionOffset,
        CancellationToken cancellationToken)
    {
        var drafts = TokenChunker.Split(
            page.Content,
            page.Heading,
            _options.ChunkSizeTokens,
            _options.ChunkOverlapTokens);

        if (drafts.Count == 0)
        {
            return [];
        }

        var chunks = new List<KnowledgeChunkEmbedding>(drafts.Count);
        var pageMetadata = page.PageMetadata;

        foreach (var draft in drafts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var embedding = await _embedder
                .EmbedAsync(tenantId, draft.Content, cancellationToken)
                .ConfigureAwait(false);

            var metadata = new KnowledgeChunkMetadata(
                source.Title,
                source.Type,
                draft.Heading,
                positionOffset + draft.Position,
                draft.TokenCount,
                cursor,
                page.ExternalId,
                pageMetadata.Url,
                pageMetadata.ModifiedAt,
                pageMetadata.Author,
                pageMetadata.Space,
                pageMetadata.Folder);

            chunks.Add(new KnowledgeChunkEmbedding(
                draft.Content,
                metadata.ToJson(),
                embedding.ToArray(),
                page.ExternalId));
        }

        return chunks;
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
            "Индексирование остановлено: векторы не обрезаются и не дополняются " +
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

    private KnowledgeProviderInfo ResolveProviderInfo(string? providerType)
    {
        var provider = _providers.Resolve(providerType);
        return provider.GetSourceInfo();
    }

    private static (string? Location, string? Content) ValidateConnection(
        KnowledgeProviderInfo info,
        string? reference,
        string? content)
    {
        var locationRequired = info.Parameters.Any(parameter =>
            parameter.Required
            && string.Equals(parameter.Name, KnowledgeProviderParameterNames.Location, StringComparison.OrdinalIgnoreCase));

        var contentRequired = info.Parameters.Any(parameter =>
            parameter.Required
            && string.Equals(parameter.Name, KnowledgeProviderParameterNames.Content, StringComparison.OrdinalIgnoreCase));

        string? location = null;
        if (!string.IsNullOrWhiteSpace(reference))
        {
            location = reference.Trim();
            if (location.Length > 500)
            {
                throw new ArgumentException("ref must be at most 500 characters.", nameof(reference));
            }
        }
        else if (locationRequired)
        {
            throw new ArgumentException(
                $"ref is required for a knowledge source of type '{info.ProviderType}'.",
                nameof(reference));
        }

        string? value = null;
        if (!string.IsNullOrWhiteSpace(content))
        {
            value = content.Trim();
        }
        else if (contentRequired)
        {
            throw new ArgumentException(
                $"Для источника type={info.ProviderType} обязателен content в теле запроса.",
                nameof(content));
        }

        return (location, value);
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

    private static IReadOnlyDictionary<string, string?>? NormalizeParameters(
        IReadOnlyDictionary<string, string?>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
        {
            return null;
        }

        var normalized = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in parameters)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                continue;
            }

            normalized[pair.Key.Trim()] = Normalize(pair.Value);
        }

        return normalized.Count == 0 ? null : normalized;
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

