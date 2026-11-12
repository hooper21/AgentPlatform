using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;
using AgentPlatform.Infrastructure.Configuration;
using AgentPlatform.Infrastructure.DependencyInjection;
using AgentPlatform.Infrastructure.Persistence.Entities;
using AgentPlatform.Infrastructure.Seeding;
using AgentPlatform.TenantApp.Web;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPlatformInfrastructure(PlatformInfrastructureOptions.FromEnvironment());

var app = builder.Build();

// dotnet run -- --migrate --seed
if (await DataMaintenance.TryRunAsync(app.Services, args))
{
    return;
}

app.UseMiddleware<TenantHeaderMiddleware>();

app.MapGet("/", () => Results.Ok(new
{
    service = "tenant-app",
    status = "stub",
    endpoints = new[] { "/health", "/api/v1/agents", "/api/v1/approvals/pending", "/api/v1/session-context" }
}));

app.MapGet("/health", () => Results.Ok(new ServiceHealth("ok", "tenant-app", DateTimeOffset.UtcNow)));

// Tenant-изоляция: репозиторий сам добавляет фильтр tenant_id из ITenantContext.
app.MapGet("/api/v1/agents", async (ITenantScopedRepository<Agent> agents, CancellationToken cancellationToken) =>
{
    var list = await agents.ListAsync(cancellationToken);
    return Results.Ok(list.Select(agent => new
    {
        agent.Id,
        agent.Name,
        agent.CurrentVersion
    }));
});

app.MapGet("/api/v1/approvals/pending", async (
    ITenantScopedRepository<Approval> approvals,
    CancellationToken cancellationToken) =>
{
    var pendingStatus = ApprovalStatus.Pending.ToDbValue();
    var pending = await approvals.Query
        .Where(approval => approval.Status == pendingStatus)
        .ToListAsync(cancellationToken);

    return Results.Ok(pending.Select(approval => new
    {
        approval.Id,
        approval.SessionId,
        approval.RequestedAt,
        approval.ExpiresAt
    }));
});

app.MapGet("/api/v1/session-context", (ITenantContext tenantContext) =>
    Results.Ok(new { tenantId = tenantContext.TenantId, resolved = tenantContext.IsResolved }));

// TODO(2.x): кабинет тенанта — CRUD агентов/каналов/знаний, операторская панель, SPA.
app.Run();

