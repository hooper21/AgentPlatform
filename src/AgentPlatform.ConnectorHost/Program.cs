using AgentPlatform.ConnectorHost.Tools;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Json;
using AgentPlatform.Core.Validation;
using ModelContextProtocol.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// MCP-сервер платформы (streamable HTTP, ModelContextProtocol 2.x).
// В v1 зарегистрированы только внутренние тестовые тулы EchoTools.
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<EchoTools>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new ServiceHealth("ok", "connector-host", DateTimeOffset.UtcNow)));

// Манифест тестового коннектора: демонстрирует контракт ConnectorManifest
// (name, version, description, auth, tools[] с JSON Schema и политиками,
// limits, cacheClasses) и проходит его валидацию при старте — как при установке.
// Тот же манифест используется тестами графа (EchoToolsManifest.Create).
var echoManifest = EchoToolsManifest.Create();

ConnectorManifestValidator.Validate(echoManifest);

// JSON Schema манифеста — артефакт платформы, публикуется вместе с каталогом коннекторов.
app.MapGet("/api/v1/connectors/manifest-schema", () => Results.Text(
    ConnectorManifestSchema.Json,
    "application/schema+json"));

// Манифест отдаётся в том же виде, в котором он проверяется и лежит в jsonb
// (snake_case + токены enum), — по нему применима опубликованная схема.
app.MapGet("/api/v1/connectors/echo/manifest", () => Results.Json(echoManifest, PlatformJson.DbOptions));

// TODO(2.2): подключение внешних MCP-серверов (external_mcp), health-check установок,
// применение лимитов и кеша read-тулов.
// MCP endpoint фиксирован (/mcp): Controller обращается к нему как к Connector Host.
app.MapMcp("/mcp");

app.Run();

