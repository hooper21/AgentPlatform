using AgentPlatform.Controller.Agents;
using AgentPlatform.Controller.Contracts;
using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Json;
using AgentPlatform.Core.Policy;
using AgentPlatform.Infrastructure.Persistence;

namespace AgentPlatform.Controller.Endpoints;

/// <summary>
/// Controller API: CRUD агентов тенанта. Изменение публикует новую версию,
/// политики тулов проверяются серверно (повышение запрещено).
/// </summary>
public static class AgentEndpoints
{
    /// <summary>Регистрирует tenant-first CRUD агентов.</summary>
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        const string route = "/api/v1/internal/tenants/{tenantId:guid}/agents";

        app.MapGet(route, ListAsync);
        app.MapPost(route, CreateAsync);
        app.MapGet($"{route}/{{agentId:guid}}", FindAsync);
        app.MapPut($"{route}/{{agentId:guid}}", UpdateAsync);
        app.MapDelete($"{route}/{{agentId:guid}}", DeleteAsync);

        return app;
    }

    private static async Task<IResult> ListAsync(
        Guid tenantId,
        AgentDefinitionService service,
        AgentPlatformDbContext context,
        CancellationToken cancellationToken)
    {
        if (!await EndpointSupport.TenantExistsAsync(context, tenantId, cancellationToken).ConfigureAwait(false))
        {
            return EndpointSupport.TenantNotFound(tenantId);
        }

        return Results.Ok(await service.ListAsync(tenantId, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> FindAsync(
        Guid tenantId,
        Guid agentId,
        AgentDefinitionService service,
        AgentPlatformDbContext context,
        CancellationToken cancellationToken)
    {
        if (!await EndpointSupport.TenantExistsAsync(context, tenantId, cancellationToken).ConfigureAwait(false))
        {
            return EndpointSupport.TenantNotFound(tenantId);
        }

        var agent = await service.GetAsync(tenantId, agentId, cancellationToken).ConfigureAwait(false);

        return agent is null
            ? Results.NotFound(new { error = "agent_not_found", tenantId, agentId })
            : Results.Ok(agent);
    }

    private static async Task<IResult> CreateAsync(
        Guid tenantId,
        AgentUpsertRequest request,
        AgentDefinitionService service,
        AgentPlatformDbContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await EndpointSupport.TenantExistsAsync(context, tenantId, cancellationToken).ConfigureAwait(false))
        {
            return EndpointSupport.TenantNotFound(tenantId);
        }

        try
        {
            var created = await service
                .CreateAsync(tenantId, request, request.ActorId, cancellationToken)
                .ConfigureAwait(false);

            return Results.Created(
                $"/api/v1/internal/tenants/{tenantId}/agents/{created.Id}",
                created);
        }
        catch (PolicyEscalationException exception)
        {
            return PolicyEscalation(exception);
        }
        catch (JsonValidationException exception)
        {
            return InvalidConfiguration(exception);
        }
        catch (AgentValidationException exception)
        {
            return AgentError(exception);
        }
        catch (ArgumentException exception)
        {
            return EndpointSupport.InvalidRequest("invalid_request", exception.Message);
        }
    }

    private static async Task<IResult> UpdateAsync(
        Guid tenantId,
        Guid agentId,
        AgentUpsertRequest request,
        AgentDefinitionService service,
        AgentPlatformDbContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await EndpointSupport.TenantExistsAsync(context, tenantId, cancellationToken).ConfigureAwait(false))
        {
            return EndpointSupport.TenantNotFound(tenantId);
        }

        try
        {
            var updated = await service
                .UpdateAsync(tenantId, agentId, request, request.ActorId, cancellationToken)
                .ConfigureAwait(false);

            return updated is null
                ? Results.NotFound(new { error = "agent_not_found", tenantId, agentId })
                : Results.Ok(updated);
        }
        catch (PolicyEscalationException exception)
        {
            return PolicyEscalation(exception);
        }
        catch (JsonValidationException exception)
        {
            return InvalidConfiguration(exception);
        }
        catch (AgentValidationException exception)
        {
            return AgentError(exception);
        }
        catch (ArgumentException exception)
        {
            return EndpointSupport.InvalidRequest("invalid_request", exception.Message);
        }
    }

    private static async Task<IResult> DeleteAsync(
        Guid tenantId,
        Guid agentId,
        AgentDefinitionService service,
        AgentPlatformDbContext context,
        CancellationToken cancellationToken)
    {
        if (!await EndpointSupport.TenantExistsAsync(context, tenantId, cancellationToken).ConfigureAwait(false))
        {
            return EndpointSupport.TenantNotFound(tenantId);
        }

        try
        {
            var deleted = await service.DeleteAsync(tenantId, agentId, cancellationToken).ConfigureAwait(false);

            return deleted
                ? Results.NoContent()
                : Results.NotFound(new { error = "agent_not_found", tenantId, agentId });
        }
        catch (AgentValidationException exception)
        {
            return AgentError(exception);
        }
    }

    private static IResult PolicyEscalation(PolicyEscalationException exception) => Results.BadRequest(new
    {
        error = "policy_escalation",
        message = exception.Message,
        manifestPolicy = exception.ManifestPolicy.ToDbValue(),
        tenantPolicy = exception.TenantPolicy.ToDbValue()
    });

    private static IResult InvalidConfiguration(JsonValidationException exception) => Results.UnprocessableEntity(new
    {
        error = "invalid_agent_configuration",
        field = exception.FieldName,
        message = exception.Message
    });

    private static IResult AgentError(AgentValidationException exception)
    {
        var conflict = exception.Code is "agent_has_sessions" or "agent_has_schedules";

        return conflict
            ? Results.Conflict(new { error = exception.Code, message = exception.Message })
            : Results.BadRequest(new { error = exception.Code, message = exception.Message });
    }
}
