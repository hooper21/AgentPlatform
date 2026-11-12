using AgentPlatform.Controller.Contracts;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Scheduling;
using AgentPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Controller.Endpoints;

/// <summary>
/// Controller API для управления расписаниями планировщика (scheduled_runs).
/// UI пока нет: расписания создаются, читаются, изменяются и удаляются здесь.
/// <para>
/// Маршруты tenant-first: Controller — внутренний API платформы, tenant_id
/// приходит из пути и используется во всех запросах к расписаниям.
/// </para>
/// </summary>
public static class ScheduledRunEndpoints
{
    /// <summary>Регистрирует CRUD-маршруты scheduled_runs.</summary>
    public static IEndpointRouteBuilder MapScheduledRunEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        const string route = "/api/v1/internal/tenants/{tenantId:guid}/scheduled-runs";

        app.MapGet(route, ListAsync);
        app.MapPost(route, CreateAsync);
        app.MapGet($"{route}/{{id:guid}}", FindAsync);
        app.MapPut($"{route}/{{id:guid}}", UpdateAsync);
        app.MapDelete($"{route}/{{id:guid}}", DeleteAsync);

        return app;
    }

    private static async Task<IResult> ListAsync(
        Guid tenantId,
        IScheduledRunStore store,
        AgentPlatformDbContext context,
        CancellationToken cancellationToken)
    {
        if (!await TenantExistsAsync(context, tenantId, cancellationToken).ConfigureAwait(false))
        {
            return TenantNotFound(tenantId);
        }

        var runs = await store.ListAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(runs.Select(ToResponse));
    }

    private static async Task<IResult> CreateAsync(
        Guid tenantId,
        ScheduledRunCreateRequest request,
        IScheduledRunStore store,
        AgentPlatformDbContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.AgentId == Guid.Empty)
        {
            return Results.BadRequest(new { error = "agent_id is required" });
        }

        if (ValidateCron(request.CronExpression) is { } cronErrorResult)
        {
            return cronErrorResult;
        }

        var validation = await ValidateTargetAsync(context, tenantId, request.AgentId, cancellationToken)
            .ConfigureAwait(false);

        if (validation is not null)
        {
            return validation;
        }

        var created = await store
            .CreateAsync(tenantId, request.AgentId, request.CronExpression, request.Enabled, cancellationToken)
            .ConfigureAwait(false);

        return Results.Created(
            $"/api/v1/internal/tenants/{tenantId}/scheduled-runs/{created.Id}",
            ToResponse(created));
    }

    private static async Task<IResult> FindAsync(
        Guid tenantId,
        Guid id,
        IScheduledRunStore store,
        CancellationToken cancellationToken)
    {
        var run = await store.FindAsync(tenantId, id, cancellationToken).ConfigureAwait(false);

        return run is null ? RunNotFound(tenantId, id) : Results.Ok(ToResponse(run));
    }

    private static async Task<IResult> UpdateAsync(
        Guid tenantId,
        Guid id,
        ScheduledRunUpdateRequest request,
        IScheduledRunStore store,
        AgentPlatformDbContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.AgentId == Guid.Empty)
        {
            return Results.BadRequest(new { error = "agent_id is required" });
        }

        if (ValidateCron(request.CronExpression) is { } cronErrorResult)
        {
            return cronErrorResult;
        }

        var validation = await ValidateTargetAsync(context, tenantId, request.AgentId, cancellationToken)
            .ConfigureAwait(false);

        if (validation is not null)
        {
            return validation;
        }

        var updated = await store
            .UpdateAsync(tenantId, id, request.AgentId, request.CronExpression, request.Enabled, cancellationToken)
            .ConfigureAwait(false);

        return updated is null ? RunNotFound(tenantId, id) : Results.Ok(ToResponse(updated));
    }

    private static async Task<IResult> DeleteAsync(
        Guid tenantId,
        Guid id,
        IScheduledRunStore store,
        CancellationToken cancellationToken)
    {
        var deleted = await store.DeleteAsync(tenantId, id, cancellationToken).ConfigureAwait(false);

        return deleted ? Results.NoContent() : RunNotFound(tenantId, id);
    }

    private static IResult? ValidateCron(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression) || expression.Trim().Length > 100)
        {
            return Results.BadRequest(new
            {
                error = "invalid_cron",
                message = "cron expression must be 1..100 characters"
            });
        }

        return CronSchedule.TryParse(expression, out _, out var error)
            ? null
            : Results.BadRequest(new { error = "invalid_cron", message = error });
    }

    private static async Task<IResult?> ValidateTargetAsync(
        AgentPlatformDbContext context,
        Guid tenantId,
        Guid agentId,
        CancellationToken cancellationToken)
    {
        if (!await TenantExistsAsync(context, tenantId, cancellationToken).ConfigureAwait(false))
        {
            return TenantNotFound(tenantId);
        }

        var agentExists = await context.Agents
            .AsNoTracking()
            .AnyAsync(agent => agent.Id == agentId && agent.TenantId == tenantId, cancellationToken)
            .ConfigureAwait(false);

        return agentExists
            ? null
            : Results.NotFound(new { error = "agent is not found in tenant", tenantId, agentId });
    }

    private static Task<bool> TenantExistsAsync(
        AgentPlatformDbContext context,
        Guid tenantId,
        CancellationToken cancellationToken)
        => context.Tenants.AsNoTracking().AnyAsync(tenant => tenant.Id == tenantId, cancellationToken);

    private static IResult TenantNotFound(Guid tenantId)
        => Results.NotFound(new { error = "tenant is not found", tenantId });

    private static IResult RunNotFound(Guid tenantId, Guid id)
        => Results.NotFound(new { error = "scheduled run is not found", tenantId, id });

    private static ScheduledRunResponse ToResponse(ScheduledRunInfo run) => new(
        run.Id,
        run.TenantId,
        run.AgentId,
        run.CronExpression,
        run.Enabled,
        run.LastRunAt,
        run.LastRunStatus,
        run.CreatedAt,
        run.UpdatedAt);
}
