using AgentPlatform.ConnectorHost.Tools;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;
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
// (name, version, auth, tools[] с JSON Schema и политиками, limits, cacheClasses).
var echoManifest = new ConnectorManifest(
    Name: "echo",
    Version: "1.0.0",
    Publisher: "AgentPlatform",
    Auth: new ConnectorAuthRequirement(ConnectorAuthType.ExternalMcp),
    Tools:
    [
        new ConnectorToolManifest(
            Name: "echo_read",
            Description: "Echoes the message back. Read-only test tool.",
            Policy: ToolPolicy.Read,
            ArgsSchema: """{"type":"object","properties":{"message":{"type":"string"}},"required":["message"]}""",
            CacheClass: "echo-read"),
        new ConnectorToolManifest(
            Name: "echo_write",
            Description: "Echoes the message back. Write tool, always gated by HITL.",
            Policy: ToolPolicy.Write,
            ArgsSchema: """{"type":"object","properties":{"message":{"type":"string"}},"required":["message"]}""")
    ],
    Limits: new ConnectorLimits(RequestsPerMinute: 60, MaxConcurrency: 4, RetryAttempts: 2),
    CacheClasses: new Dictionary<string, string>(StringComparer.Ordinal) { ["echo-read"] = "60s" });

app.MapGet("/api/v1/connectors/echo/manifest", () => Results.Ok(echoManifest));

// TODO(2.2): подключение внешних MCP-серверов (external_mcp), health-check установок,
// применение лимитов и кеша read-тулов.
app.MapMcp();

app.Run();

