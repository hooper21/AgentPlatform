using AgentPlatform.Controller.Connectors;
using AgentPlatform.Controller.Contracts;
using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Json;
using AgentPlatform.Core.Policy;
using AgentPlatform.Infrastructure.Persistence;

namespace AgentPlatform.Controller.Endpoints;

/// <summary>
/// Controller API: каталог коннекторов, установки с credentials и tool_settings.
/// Credentials принимаются только на вход и наружу не возвращаются.
/// </summary>
public static class ConnectorEndpoints
{
    /// <summary>Регистрирует tenant-first CRUD коннекторов.</summary>
    public static IEndpointRouteBuilder MapConnectorEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        const string tenantRoute = "/api/v1/internal/tenants/{tenantId:guid}";
        const string installations = tenantRoute + "/connector-installations";
        const string toolSettings = installations + "/{installationId:guid}/tool-settings";

        app.MapGet(tenantRoute + "/connector-registry", ListCatalogAsync);
        app.MapPost("/api/v1/internal/connector-registry/sync", SyncRegistryAsync);
        app.MapGet(installations, ListInstallationsAsync);
        app.MapPost(installations, InstallAsync);
        app.MapPut($"{installations}/{{installationId:guid}}", UpdateAsync);
        app.MapDelete($"{installations}/{{installationId:guid}}", DeleteAsync);
        app.MapGet(toolSettings, ListToolSettingsAsync);
        app.MapPut($"{toolSettings}/{{toolName}}", UpsertToolSettingAsync);
        app.MapDelete($"{toolSettings}/{{toolName}}", DeleteToolSettingAsync);

        return app;
    }

    private static async Task<IResult> ListCatalogAsync(
        Guid tenantId,
        ConnectorService service,
        AgentPlatformDbContext context,
        CancellationToken cancellationToken)
    {
        if (!await EndpointSupport.TenantExistsAsync(context, tenantId, cancellationToken).ConfigureAwait(false))
        {
            return EndpointSupport.TenantNotFound(tenantId);
        }

        return Results.Ok(await service.ListCatalogAsync(tenantId, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> ListInstallationsAsync(
        Guid tenantId,
        ConnectorService service,
        AgentPlatformDbContext context,
        CancellationToken cancellationToken)
    {
        if (!await EndpointSupport.TenantExistsAsync(context, tenantId, cancellationToken).ConfigureAwait(false))
        {
            return EndpointSupport.TenantNotFound(tenantId);
        }

        return Results.Ok(await service.ListInstallationsAsync(tenantId, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> InstallAsync(
        Guid tenantId,
        ConnectorInstallRequest request,
        ConnectorService service,
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
            var created = await service.InstallAsync(tenantId, request, cancellationToken).ConfigureAwait(false);

            return Results.Created(
                $"/api/v1/internal/tenants/{tenantId}/connector-installations/{created.Id}",
                created);
        }
        catch (ConnectorValidationException exception)
        {
            return ConnectorError(exception);
        }
        catch (JsonValidationException exception)
        {
            return InvalidManifest(exception);
        }
        catch (PolicyEscalationException exception)
        {
            return PolicyEscalation(exception);
        }
    }

    private static async Task<IResult> SyncRegistryAsync(
        ConnectorRegistrySyncService sync,
        CancellationToken cancellationToken)
    {
        var synced = await sync.SyncEchoAsync(cancellationToken).ConfigureAwait(false);

        return synced is null
            ? Results.Ok(new
            {
                synced = false,
                message = "Connector Host is unavailable; the registry was not changed."
            })
            : Results.Ok(new { synced = true, connector = synced });
    }

    private static IResult ConnectorError(ConnectorValidationException exception)
    {
        var conflict = exception.Code is "connector_already_installed" or "connector_in_use";

        return conflict
            ? Results.Conflict(new { error = exception.Code, message = exception.Message })
            : Results.BadRequest(new { error = exception.Code, message = exception.Message });
    }

    private static IResult InvalidManifest(JsonValidationException exception) => Results.UnprocessableEntity(new
    {
        error = "invalid_connector_manifest",
        field = exception.FieldName,
        message = exception.Message
    });

    private static IResult PolicyEscalation(PolicyEscalationException exception) => Results.BadRequest(new
    {
        error = "policy_escalation",
        message = exception.Message,
        manifestPolicy = exception.ManifestPolicy.ToDbValue(),
        tenantPolicy = exception.TenantPolicy.ToDbValue()
    });

    private static async Task<IResult> UpdateAsync(
        Guid tenantId,
        Guid installationId,
        ConnectorInstallationUpdateRequest request,
        ConnectorService service,
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
                .UpdateAsync(tenantId, installationId, request, cancellationToken)
                .ConfigureAwait(false);

            return updated is null
                ? Results.NotFound(new { error = "connector_installation_not_found", tenantId, installationId })
                : Results.Ok(updated);
        }
        catch (ConnectorValidationException exception)
        {
            return ConnectorError(exception);
        }
        catch (JsonValidationException exception)
        {
            return InvalidManifest(exception);
        }
    }

    private static async Task<IResult> DeleteAsync(
        Guid tenantId,
        Guid installationId,
        ConnectorService service,
        AgentPlatformDbContext context,
        CancellationToken cancellationToken)
    {
        if (!await EndpointSupport.TenantExistsAsync(context, tenantId, cancellationToken).ConfigureAwait(false))
        {
            return EndpointSupport.TenantNotFound(tenantId);
        }

        try
        {
            var deleted = await service
                .DeleteAsync(tenantId, installationId, cancellationToken)
                .ConfigureAwait(false);

            return deleted
                ? Results.NoContent()
                : Results.NotFound(new { error = "connector_installation_not_found", tenantId, installationId });
        }
        catch (ConnectorValidationException exception)
        {
            return ConnectorError(exception);
        }
    }

    private static async Task<IResult> ListToolSettingsAsync(
        Guid tenantId,
        Guid installationId,
        ConnectorService service,
        CancellationToken cancellationToken)
    {
        var settings = await service
            .ListToolSettingsAsync(tenantId, installationId, cancellationToken)
            .ConfigureAwait(false);

        return settings is null
            ? Results.NotFound(new { error = "connector_installation_not_found", tenantId, installationId })
            : Results.Ok(settings);
    }

    private static async Task<IResult> UpsertToolSettingAsync(
        Guid tenantId,
        Guid installationId,
        string toolName,
        ToolSettingUpsertRequest request,
        ConnectorService service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var updated = await service
                .UpsertToolSettingAsync(tenantId, installationId, toolName, request, cancellationToken)
                .ConfigureAwait(false);

            return updated is null
                ? Results.NotFound(new { error = "connector_installation_not_found", tenantId, installationId })
                : Results.Ok(updated);
        }
        catch (PolicyEscalationException exception)
        {
            return PolicyEscalation(exception);
        }
        catch (JsonValidationException exception)
        {
            return Results.UnprocessableEntity(new
            {
                error = "invalid_tool_settings",
                field = exception.FieldName,
                message = exception.Message
            });
        }
        catch (ConnectorValidationException exception)
        {
            return ConnectorError(exception);
        }
    }

    private static async Task<IResult> DeleteToolSettingAsync(
        Guid tenantId,
        Guid installationId,
        string toolName,
        ConnectorService service,
        CancellationToken cancellationToken)
    {
        var deleted = await service
            .DeleteToolSettingAsync(tenantId, installationId, toolName, cancellationToken)
            .ConfigureAwait(false);

        return deleted
            ? Results.NoContent()
            : Results.NotFound(new
            {
                error = "tool_setting_not_found",
                tenantId,
                installationId,
                toolName
            });
    }
}
