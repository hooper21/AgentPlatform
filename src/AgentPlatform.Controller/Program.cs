using AgentPlatform.Controller.Composition;
using AgentPlatform.Controller.Endpoints;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Dto;
using AgentPlatform.Infrastructure.Configuration;
using AgentPlatform.Infrastructure.DependencyInjection;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Persistence.Entities;
using AgentPlatform.Infrastructure.Seeding;
using AgentPlatform.Runtime;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Внутренний API платформы: работает с глобальными таблицами и cross-tenant выборками.
builder.Services.AddPlatformInfrastructure(PlatformInfrastructureOptions.FromEnvironment());

// RAG-порт Knowledge Service: диалоги кабинета получают справочный контекст.
builder.Services.AddPlatformKnowledgeRag(builder.Configuration["Knowledge:BaseUrl"]);

// Runtime, Secret Service и MCP-транспорт для диалогов/гейтов кабинета.
builder.Services.AddControllerServices(builder.Configuration);

var app = builder.Build();

if (await DataMaintenance.TryRunAsync(app.Services, args))
{
    return;
}

app.MapGet("/", () => Results.Ok(new
{
    service = "controller",
    status = "stub",
    endpoints = new[]
    {
        "/health",
        "/api/v1/internal/tenants",
        "/api/v1/internal/tenants/{tenantId}/users",
        "/api/v1/internal/tool-settings/validate",
        "GET/POST /api/v1/internal/tenants/{tenantId}/scheduled-runs",
        "GET/PUT/DELETE /api/v1/internal/tenants/{tenantId}/scheduled-runs/{id}",
        "GET/POST /api/v1/internal/tenants/{tenantId}/agents",
        "GET/PUT/DELETE /api/v1/internal/tenants/{tenantId}/agents/{id}",
        "GET /api/v1/internal/tenants/{tenantId}/connector-registry",
        "GET/POST /api/v1/internal/tenants/{tenantId}/connector-installations",
        "GET/PUT/DELETE /api/v1/internal/tenants/{tenantId}/connector-installations/{id}/tool-settings",
        "GET/POST /api/v1/internal/tenants/{tenantId}/dialogs",
        "GET /api/v1/internal/tenants/{tenantId}/dialogs/{sessionId}",
        "POST /api/v1/internal/tenants/{tenantId}/dialogs/{sessionId}/approvals/{approvalId}/decision"
    }
}));

app.MapGet("/health", () => Results.Ok(new ServiceHealth("ok", "controller", DateTimeOffset.UtcNow)));

// Проверка seed: список тенантов платформы (глобальная таблица, без tenant-фильтра).
app.MapGet("/api/v1/internal/tenants", async (
    IGlobalRepository<Tenant> tenants,
    CancellationToken cancellationToken) =>
{
    var list = await tenants.ListAsync(cancellationToken);
    return Results.Ok(list.Select(tenant => new
    {
        tenant.Id,
        tenant.Name,
        tenant.Plan,
        tenant.Status,
        tenant.CreatedAt
    }));
});

// Проверка seed: пользователи тенанта. Хеши паролей не отдаются наружу.
app.MapGet("/api/v1/internal/tenants/{tenantId:guid}/users", async (
    Guid tenantId,
    AgentPlatformDbContext context,
    CancellationToken cancellationToken) =>
{
    var users = await context.Users
        .Where(user => user.TenantId == tenantId)
        .OrderBy(user => user.Email)
        .Select(user => new { user.Id, user.Email, user.Role, user.CreatedAt })
        .ToListAsync(cancellationToken);

    return Results.Ok(users);
});

// Policy Engine (Core + Runtime): проверка понижения политики и gate_settings
// перед сохранением tool_settings. Runtime повторяет проверку при вызове тула.
app.MapToolSettingEndpoints();

// Scheduler: управление расписаниями cron-запусков (scheduled_runs) без UI.
app.MapScheduledRunEndpoints();

// Кабинет тенанта: агенты, коннекторы, tool_settings и диалоги с HITL-гейтами.
app.MapAgentEndpoints();
app.MapConnectorEndpoints();
app.MapCabinetDialogEndpoints();

// TODO(3.x): панель оператора платформы — шаблоны решений, аудит платформы.
app.Run();

