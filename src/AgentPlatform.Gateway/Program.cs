using AgentPlatform.Core.Dto;
using AgentPlatform.Gateway.Caching;
using AgentPlatform.Gateway.Composition;
using AgentPlatform.Gateway.Endpoints;
using AgentPlatform.Infrastructure.Configuration;
using AgentPlatform.Infrastructure.DependencyInjection;
using AgentPlatform.Runtime;

var builder = WebApplication.CreateBuilder(args);

// Redis: rate-limit тенантов и кеш конфигурации агента (соединение ленивое).
builder.Services.AddPlatformRedis(builder.Configuration);

// PostgreSQL: сессии, сообщения, чекпоинты, заявки HITL, конфигурации агентов.
builder.Services.AddPlatformInfrastructure(PlatformInfrastructureOptions.FromEnvironment());

// Порты Runtime: EF-хранилища и Connector Host (TODO 2.2 — MCP-транспорт).
builder.Services.AddGatewayRuntimePorts();

// Gateway: JWT виджета, rate-limit, сессии, SSE-поток, повторы LLM.
builder.Services.AddGatewaySessions(builder.Configuration);
builder.Services.AddGatewayLlmResilience(builder.Configuration);

// RAG-порт Knowledge Service: справочный блок подставляется перед LLM-ходом.
builder.Services.AddPlatformKnowledgeRag(builder.Configuration["Knowledge:BaseUrl"]);

// Runtime агентов за контрактом IOrchestrationRuntime.
builder.Services.AddMafAgentRuntime(new AgentRuntimeOptions
{
    RedisConnectionString = builder.Configuration["Redis:ConnectionString"]
});

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "gateway",
    status = "ready",
    endpoints = new[]
    {
        "/health",
        "POST /api/sessions/{tenantKey}/messages",
        "GET /api/sessions/{sessionId}/stream"
    }
}));

app.MapGet("/health", () => Results.Ok(new ServiceHealth("ok", "gateway", DateTimeOffset.UtcNow)));

app.MapGatewayEndpoints();

app.Run();


