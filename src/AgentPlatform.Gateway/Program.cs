using AgentPlatform.Core.Dto;
using AgentPlatform.Gateway.Caching;

var builder = WebApplication.CreateBuilder(args);

// Redis (кеши, rate-limit) подключается лениво: для healthcheck он не обязателен.
builder.Services.AddPlatformRedis(builder.Configuration);

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "gateway",
    status = "stub",
    endpoints = new[] { "/health", "/api/v1/sessions/{sessionId}/messages" }
}));

app.MapGet("/health", () => Results.Ok(new ServiceHealth("ok", "gateway", DateTimeOffset.UtcNow)));

// TODO(2.x): приём сообщения из канала, проверка подписи/JWT, запуск IOrchestrationRuntime.
app.MapPost("/api/v1/sessions/{sessionId:guid}/messages", (Guid sessionId, InboundMessage message) =>
    Results.Problem(
        title: "Gateway is a stub in v1",
        detail: $"Session {sessionId} received a message; orchestration runtime is wired in task 2.x.",
        statusCode: StatusCodes.Status501NotImplemented));

app.Run();

