using AgentPlatform.Controller.Contracts;
using AgentPlatform.Controller.Dialogs;
using AgentPlatform.Infrastructure.Persistence;

namespace AgentPlatform.Controller.Endpoints;

/// <summary>
/// Controller API диалогов кабинета: список сессий, сообщения и карточка гейта.
/// Подтверждение/отклонение вызывает <c>IOrchestrationRuntime.ResumeAsync</c>.
/// </summary>
public static class CabinetDialogEndpoints
{
    /// <summary>Регистрирует tenant-first маршруты диалогов.</summary>
    public static IEndpointRouteBuilder MapCabinetDialogEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        const string route = "/api/v1/internal/tenants/{tenantId:guid}/dialogs";

        app.MapGet(route, ListAsync);
        app.MapPost(route, StartAsync);
        app.MapGet($"{route}/{{sessionId:guid}}", FindAsync);
        app.MapPost($"{route}/{{sessionId:guid}}/messages", SendMessageAsync);
        app.MapPost(
            $"{route}/{{sessionId:guid}}/approvals/{{approvalId:guid}}/decision",
            DecideAsync);

        return app;
    }

    private static async Task<IResult> ListAsync(
        Guid tenantId,
        int? limit,
        CabinetDialogService service,
        AgentPlatformDbContext context,
        CancellationToken cancellationToken)
    {
        if (!await EndpointSupport.TenantExistsAsync(context, tenantId, cancellationToken).ConfigureAwait(false))
        {
            return EndpointSupport.TenantNotFound(tenantId);
        }

        var sessions = await service
            .ListAsync(tenantId, limit ?? 100, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(sessions);
    }

    private static async Task<IResult> StartAsync(
        Guid tenantId,
        CabinetDialogStartRequest request,
        CabinetDialogService service,
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
            var dialog = await service.StartAsync(tenantId, request, cancellationToken).ConfigureAwait(false);

            return Results.Created(
                $"/api/v1/internal/tenants/{tenantId}/dialogs/{dialog.SessionId}",
                dialog);
        }
        catch (CabinetDialogException exception)
        {
            return DialogError(exception);
        }
        catch (ArgumentException exception)
        {
            return EndpointSupport.InvalidRequest("invalid_request", exception.Message);
        }
    }

    private static async Task<IResult> FindAsync(
        Guid tenantId,
        Guid sessionId,
        CabinetDialogService service,
        AgentPlatformDbContext context,
        CancellationToken cancellationToken)
    {
        if (!await EndpointSupport.TenantExistsAsync(context, tenantId, cancellationToken).ConfigureAwait(false))
        {
            return EndpointSupport.TenantNotFound(tenantId);
        }

        var dialog = await service.GetAsync(tenantId, sessionId, cancellationToken).ConfigureAwait(false);

        return dialog is null ? SessionNotFound(tenantId, sessionId) : Results.Ok(dialog);
    }

    private static async Task<IResult> SendMessageAsync(
        Guid tenantId,
        Guid sessionId,
        CabinetDialogMessageRequest request,
        CabinetDialogService service,
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
            var dialog = await service
                .SendMessageAsync(tenantId, sessionId, request.Message, cancellationToken)
                .ConfigureAwait(false);

            return dialog is null ? SessionNotFound(tenantId, sessionId) : Results.Ok(dialog);
        }
        catch (CabinetDialogException exception)
        {
            return DialogError(exception);
        }
    }

    private static async Task<IResult> DecideAsync(
        Guid tenantId,
        Guid sessionId,
        Guid approvalId,
        CabinetApprovalDecisionRequest request,
        CabinetDialogService service,
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
            var dialog = await service
                .DecideAsync(tenantId, sessionId, approvalId, request, cancellationToken)
                .ConfigureAwait(false);

            return dialog is null ? SessionNotFound(tenantId, sessionId) : Results.Ok(dialog);
        }
        catch (CabinetDialogException exception)
        {
            return DialogError(exception);
        }
        catch (InvalidOperationException exception)
        {
            // Runtime отклонил resume (сессия закрыта, гейт уже решён, тул недоступен).
            return Results.Conflict(new { error = "resume_rejected", message = exception.Message });
        }
    }

    private static IResult DialogError(CabinetDialogException exception) => exception.Code switch
    {
        "agent_not_found" or "approval_not_found" =>
            Results.NotFound(new { error = exception.Code, message = exception.Message }),
        "invalid_decision" or "message_required" or "message_too_long" or "agent_id_required"
            or "actor_required" or "actor_not_found" =>
            Results.BadRequest(new { error = exception.Code, message = exception.Message }),
        "actor_forbidden" =>
            Results.Json(
                new { error = exception.Code, message = exception.Message },
                statusCode: StatusCodes.Status403Forbidden),
        _ => Results.Conflict(new { error = exception.Code, message = exception.Message })
    };

    private static IResult SessionNotFound(Guid tenantId, Guid sessionId)
        => Results.NotFound(new { error = "dialog_not_found", tenantId, sessionId });
}
