using AgentPlatform.Core.Dto;
using AgentPlatform.Infrastructure.Configuration;
using AgentPlatform.Infrastructure.DependencyInjection;
using AgentPlatform.Infrastructure.Tenancy;
using AgentPlatform.Knowledge.Composition;
using AgentPlatform.Knowledge.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// PostgreSQL + pgvector: knowledge_sources/knowledge_chunks/rag_misses, tenant-контекст.
builder.Services.AddPlatformInfrastructure(PlatformInfrastructureOptions.FromEnvironment());

// Инжест (document | site | faq), эмбеддер через IChatClient, RAG-поиск, переиндексация.
builder.Services.AddKnowledgeService(builder.Configuration);

var app = builder.Build();

// v1: tenant определяется заголовком X-Tenant-Id (заглушка аутентификации, см. README).
// Все операции Knowledge Service идут только в рамках установленного тенанта.
app.Use(async (context, next) =>
{
    if (context.Request.Headers.TryGetValue(KnowledgeEndpoints.TenantHeader, out var values)
        && Guid.TryParse(values.ToString(), out var tenantId)
        && tenantId != Guid.Empty)
    {
        context.RequestServices.GetRequiredService<TenantContext>().SetTenant(tenantId);
    }

    await next(context);
});

app.MapGet("/", () => Results.Ok(new
{
    service = "knowledge",
    status = "ready",
    endpoints = new[]
    {
        "/health",
        "POST /api/knowledge/sources",
        "GET /api/knowledge/sources",
        "GET /api/knowledge/sources/{sourceId}",
        "POST /api/knowledge/sources/{sourceId}/reindex",
        "POST /api/knowledge/sources/{sourceId}/sync",
        "GET /api/knowledge/providers",
        "POST /api/knowledge/providers/{providerType}/test-connection",
        "POST /api/knowledge/webhooks/{sourceId}",
        "GET /api/knowledge/search?query=&topK=5&threshold=0.77"
    }
}));

app.MapGet("/health", () => Results.Ok(new ServiceHealth("ok", "knowledge", DateTimeOffset.UtcNow)));

app.MapKnowledgeEndpoints();

app.Run();


