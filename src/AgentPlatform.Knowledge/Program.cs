using AgentPlatform.Core.Dto;

var builder = WebApplication.CreateBuilder(args);

// TODO(2.3): pgvector-поиск и инжест (Knowledge Service) — здесь появится доступ к БД и embedder.
var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "knowledge",
    status = "stub",
    endpoints = new[] { "/health", "/api/v1/knowledge/search", "/api/v1/knowledge/sources/{sourceId}/reindex" }
}));

app.MapGet("/health", () => Results.Ok(new ServiceHealth("ok", "knowledge", DateTimeOffset.UtcNow)));

// Заглушка поиска: контракт зафиксирован, pgvector-запрос появится в задаче 2.3.
app.MapPost("/api/v1/knowledge/search", (KnowledgeSearchRequest request) =>
{
    ArgumentNullException.ThrowIfNull(request);

    return Results.Ok(Array.Empty<KnowledgeHit>());
});

app.MapPost("/api/v1/knowledge/sources/{sourceId:guid}/reindex", (Guid sourceId) =>
    Results.Problem(
        title: "Knowledge ingest is a stub in v1",
        detail: $"Source {sourceId} reindex is implemented in task 2.3.",
        statusCode: StatusCodes.Status501NotImplemented));

app.Run();

