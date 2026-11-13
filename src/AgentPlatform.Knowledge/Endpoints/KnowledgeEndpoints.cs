using System.Text.Json;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Persistence.Entities;
using AgentPlatform.Infrastructure.Tenancy;
using AgentPlatform.Knowledge.Contracts;
using AgentPlatform.Knowledge.Ingest;
using AgentPlatform.Knowledge.Search;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Knowledge.Endpoints;

/// <summary>
/// API Knowledge Service: инжест источников, переиндексация и RAG-поиск.
/// Тенант берётся из ITenantContext (v1 — заголовок X-Tenant-Id):
/// все операции строго tenant-scoped.
/// </summary>
public static class KnowledgeEndpoints
{
    /// <summary>Заголовок тенанта (v1-заглушка аутентификации).</summary>
    public const string TenantHeader = "X-Tenant-Id";

    /// <summary>Регистрирует /api/knowledge/*.</summary>
    public static IEndpointRouteBuilder MapKnowledgeEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/knowledge");

        group.MapPost("/sources", CreateSourceAsync);
        group.MapGet("/sources", ListSourcesAsync);
        group.MapGet("/sources/{sourceId:guid}", GetSourceAsync);
        group.MapPost("/sources/{sourceId:guid}/reindex", ReindexSourceAsync);
        group.MapPost("/sources/{sourceId:guid}/sync", SyncSourceAsync);
        group.MapGet("/providers", ListProviders);
        group.MapGet("/providers/{providerType}/setup", GetProviderSetup);
        group.MapPost("/providers/{providerType}/test-connection", TestProviderAsync);
        group.MapPost("/providers/{providerType}/spaces", ListProviderSpacesAsync);
        group.MapPost("/webhooks/{sourceId:guid}", WebhookAsync);
        group.MapGet("/search", SearchAsync);

        return app;
    }

    private static async Task<IResult> CreateSourceAsync(
        CreateKnowledgeSourceRequest request,
        ITenantContext tenant,
        KnowledgeIngestService ingest,
        ISecretService secrets,
        CancellationToken cancellationToken)
    {
        if (RequireTenant(tenant) is { } missing)
        {
            return missing;
        }

        try
        {
            request = await WithStoredCredentialsAsync(request, tenant, secrets, cancellationToken)
                .ConfigureAwait(false);

            var source = await ingest.CreateAsync(request, cancellationToken).ConfigureAwait(false);
            return Results.Created($"/api/knowledge/sources/{source.Id}", Map(source));
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = "invalid_request", message = exception.Message });
        }
    }

    private static async Task<IResult> ListSourcesAsync(
        ITenantContext tenant,
        ITenantScopedRepository<KnowledgeSource> sources,
        CancellationToken cancellationToken)
    {
        if (RequireTenant(tenant) is { } missing)
        {
            return missing;
        }

        var list = await sources.ListAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(list.Select(Map));
    }

    private static async Task<IResult> GetSourceAsync(
        Guid sourceId,
        ITenantContext tenant,
        ITenantScopedRepository<KnowledgeSource> sources,
        CancellationToken cancellationToken)
    {
        if (RequireTenant(tenant) is { } missing)
        {
            return missing;
        }

        var source = await sources.FindAsync(sourceId, cancellationToken).ConfigureAwait(false);
        return source is null
            ? Results.NotFound(new { error = "source_not_found", sourceId })
            : Results.Ok(Map(source));
    }

    private static async Task<IResult> ReindexSourceAsync(
        Guid sourceId,
        ITenantContext tenant,
        KnowledgeIngestService ingest,
        CancellationToken cancellationToken)
    {
        if (RequireTenant(tenant) is { } missing)
        {
            return missing;
        }

        var source = await ingest.ReindexAsync(sourceId, cancellationToken).ConfigureAwait(false);
        return source is null
            ? Results.NotFound(new { error = "source_not_found", sourceId })
            : Results.Ok(Map(source));
    }

    private static async Task<IResult> SyncSourceAsync(
        Guid sourceId,
        ITenantContext tenant,
        KnowledgeIngestService ingest,
        CancellationToken cancellationToken)
    {
        if (RequireTenant(tenant) is { } missing)
        {
            return missing;
        }

        var source = await ingest.SyncAsync(sourceId, cancellationToken).ConfigureAwait(false);
        return source is null
            ? Results.NotFound(new { error = "source_not_found", sourceId })
            : Results.Ok(Map(source));
    }

    private static IResult ListProviders(KnowledgeProviderRegistry providers)
        => Results.Ok(providers.Describe());

    private static IResult GetProviderSetup(string providerType, KnowledgeProviderRegistry providers)
    {
        try
        {
            var provider = providers.Resolve(providerType);

            return provider is IKnowledgeProviderSetup setup
                ? Results.Ok(new KnowledgeProviderSetupResponse(setup.ProviderType, setup.Warning))
                : Results.NotFound(new { error = "setup_not_supported", providerType });
        }
        catch (ArgumentException)
        {
            return Results.NotFound(new { error = "provider_not_found", providerType });
        }
    }

    private static async Task<IResult> TestProviderAsync(
        string providerType,
        TestKnowledgeProviderRequest request,
        ITenantContext tenant,
        KnowledgeProviderRegistry providers,
        IKnowledgeSecretResolver secrets,
        CancellationToken cancellationToken)
    {
        if (RequireTenant(tenant) is { } missing)
        {
            return missing;
        }

        try
        {
            // Проверка идёт через контракт: тот же код, что и при инжесте.
            var provider = providers.Resolve(providerType);
            var config = BuildProviderConfig(request);

            // Учётные данные мастера ещё не сохранены: живут только в scope запроса.
            secrets.UseEphemeral(request.Credentials);

            var status = await provider.TestConnection(config, cancellationToken).ConfigureAwait(false);
            return Results.Ok(status);
        }
        catch (Exception exception) when (exception is ArgumentException or KnowledgeProviderConfigurationException)
        {
            return Results.BadRequest(new { error = "invalid_request", message = exception.Message });
        }
    }

    private static async Task<IResult> ListProviderSpacesAsync(
        string providerType,
        TestKnowledgeProviderRequest request,
        ITenantContext tenant,
        KnowledgeProviderRegistry providers,
        IKnowledgeSecretResolver secrets,
        CancellationToken cancellationToken)
    {
        if (RequireTenant(tenant) is { } missing)
        {
            return missing;
        }

        try
        {
            var provider = providers.Resolve(providerType);

            if (provider is not IKnowledgeProviderSetup setup)
            {
                return Results.NotFound(new { error = "spaces_not_supported", providerType });
            }

            secrets.UseEphemeral(request.Credentials);

            var spaces = await setup
                .ListSpacesAsync(BuildProviderConfig(request), cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(spaces);
        }
        catch (Exception exception) when (exception is ArgumentException or KnowledgeProviderConfigurationException)
        {
            return Results.BadRequest(new { error = "invalid_request", message = exception.Message });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Results.Json(
                new { error = "provider_unavailable", message = exception.Message },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>
    /// Общий вебхук для всех провайдеров: маршрутизация по source_id. Тенант
    /// определяется по источнику — внешняя система не передаёт X-Tenant-Id.
    /// Confluence: page_created/page_updated запускают инкремент,
    /// page_trashed удаляет чанки страницы (tombstone).
    /// TODO(contract): проверка подписи (Connect JWT) и регистрация вебхука
    /// появятся вместе с контрактом плагина внешней системы знаний.
    /// </summary>
    private static async Task<IResult> WebhookAsync(
        Guid sourceId,
        HttpRequest httpRequest,
        AgentPlatformDbContext context,
        TenantContext tenant,
        KnowledgeIngestService ingest,
        KnowledgeProviderRegistry providers,
        CancellationToken cancellationToken)
    {
        var source = await context.KnowledgeSources
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == sourceId, cancellationToken)
            .ConfigureAwait(false);

        if (source is null)
        {
            return Results.NotFound(new { error = "source_not_found", sourceId });
        }

        tenant.SetTenant(source.TenantId);

        JsonDocument? payload = null;

        if (httpRequest.ContentLength is null or > 0)
        {
            try
            {
                payload = await JsonDocument
                    .ParseAsync(httpRequest.Body, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException)
            {
                payload = null;
            }
        }

        using (payload)
        {
            if (payload is not null)
            {
                var provider = providers.Resolve(source);

                if (provider is IKnowledgeWebhookParser parser && parser.CanParse(source.ProviderType))
                {
                    var eventHint = httpRequest.Query["event"].ToString();

                    if (!parser.TryParse(
                        payload.RootElement,
                        out var webhookEvent,
                        string.IsNullOrWhiteSpace(eventHint) ? null : eventHint))
                    {
                        // Провайдер распознаёт свои вебхуки: неизвестное событие индекс не меняет.
                        return Results.Accepted($"/api/knowledge/sources/{sourceId}");
                    }

                    if (webhookEvent.IsDeletion && !string.IsNullOrWhiteSpace(webhookEvent.ExternalId))
                    {
                        var deleted = await ingest
                            .ApplyDeletionsAsync(sourceId, [webhookEvent.ExternalId], cancellationToken)
                            .ConfigureAwait(false);

                        return deleted is null
                            ? Results.NotFound(new { error = "source_not_found", sourceId })
                            : Results.Ok(Map(deleted));
                    }

                    if (webhookEvent.EventType is KnowledgeWebhookEventTypes.Created
                        or KnowledgeWebhookEventTypes.Updated)
                    {
                        var syncedByEvent = await ingest.SyncAsync(sourceId, cancellationToken).ConfigureAwait(false);

                        return syncedByEvent is null
                            ? Results.NotFound(new { error = "source_not_found", sourceId })
                            : Results.Ok(Map(syncedByEvent));
                    }

                    // Событие не влияет на индекс.
                    return Results.Accepted($"/api/knowledge/sources/{sourceId}");
                }
            }
        }

        // Провайдер без разбора payload: прежнее поведение — инкрементальная синхронизация.
        var sourceAfterSync = await ingest.SyncAsync(sourceId, cancellationToken).ConfigureAwait(false);
        return sourceAfterSync is null
            ? Results.NotFound(new { error = "source_not_found", sourceId })
            : Results.Ok(Map(sourceAfterSync));
    }

    private static async Task<IResult> SearchAsync(
        string? query,
        int? topK,
        double? threshold,
        ITenantContext tenant,
        KnowledgeSearchService search,
        ILogger<KnowledgeSearchService> logger,
        CancellationToken cancellationToken)
    {
        if (RequireTenant(tenant) is { } missing)
        {
            return missing;
        }

        try
        {
            var hits = await search
                .SearchAsync(tenant.TenantId!.Value, query, topK, threshold, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(hits);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = "invalid_request", message = exception.Message });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Диалог не ломается: Runtime трактует недоступность поиска как пустой контекст.
            logger.LogError(exception, "Knowledge search failed for tenant {TenantId}", tenant.TenantId);
            return Results.Problem(
                title: "knowledge_search_unavailable",
                detail: "Поиск по базе знаний временно недоступен.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static KnowledgeProviderConfig BuildProviderConfig(TestKnowledgeProviderRequest request)
        => new(
            request.Title ?? string.Empty,
            request.Location,
            request.Content,
            request.CredentialsRef,
            request.Config);

    /// <summary>
    /// Шифрует учётные данные из мастера через Secret Service и подставляет
    /// credentials_ref. Значения не попадают в config/index_meta и API-ответы.
    /// </summary>
    private static async Task<CreateKnowledgeSourceRequest> WithStoredCredentialsAsync(
        CreateKnowledgeSourceRequest request,
        ITenantContext tenant,
        ISecretService secrets,
        CancellationToken cancellationToken)
    {
        if (request.Credentials is not { Count: > 0 } || tenant.TenantId is not { } tenantId)
        {
            return request;
        }

        var credentials = request.Credentials
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value.Trim(), StringComparer.OrdinalIgnoreCase);

        if (credentials.Count == 0)
        {
            return request;
        }

        var reference = await secrets.StoreAsync(tenantId, credentials, cancellationToken)
            .ConfigureAwait(false);

        return request with { CredentialsRef = reference, Credentials = null };
    }

    private static IResult? RequireTenant(ITenantContext tenant)
        => tenant.IsResolved
            ? null
            : Results.BadRequest(new { error = "tenant_required", message = $"Header {TenantHeader} is required." });

    private static KnowledgeSourceResponse Map(KnowledgeSource source) => new(
        source.Id,
        source.Type,
        string.Equals(source.Type, KnowledgeSourceType.Faq.ToDbValue(), StringComparison.Ordinal)
            ? null
            : source.Ref,
        source.Title,
        source.ChunkCount,
        source.IndexStatus,
        ParseMeta(source.IndexMeta),
        source.CreatedAt,
        source.UpdatedAt,
        source.ProviderType,
        source.SyncSchedule,
        source.LastCursor,
        ParseMeta(source.Config));

    private static JsonElement ParseMeta(string json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return document.RootElement.Clone();
    }
}
