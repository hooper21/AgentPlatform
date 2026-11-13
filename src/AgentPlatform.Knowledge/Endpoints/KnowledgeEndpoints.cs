using System.Text.Json;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;
using AgentPlatform.Infrastructure.Persistence.Entities;
using AgentPlatform.Knowledge.Contracts;
using AgentPlatform.Knowledge.Ingest;
using AgentPlatform.Knowledge.Search;

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
        group.MapGet("/search", SearchAsync);

        return app;
    }

    private static async Task<IResult> CreateSourceAsync(
        CreateKnowledgeSourceRequest request,
        ITenantContext tenant,
        KnowledgeIngestService ingest,
        CancellationToken cancellationToken)
    {
        if (RequireTenant(tenant) is { } missing)
        {
            return missing;
        }

        try
        {
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
        source.UpdatedAt);

    private static JsonElement ParseMeta(string json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return document.RootElement.Clone();
    }
}
