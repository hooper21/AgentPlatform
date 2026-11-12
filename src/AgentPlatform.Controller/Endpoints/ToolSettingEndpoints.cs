using AgentPlatform.Controller.Contracts;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Json;
using AgentPlatform.Core.Policy;
using AgentPlatform.Core.Validation;
using AgentPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Controller.Endpoints;

/// <summary>
/// Валидация конфигурации тулов тенанта в Controller API. Повышение политики
/// (read → write) отклоняется до сохранения tool_settings; Runtime повторно
/// проверяет ту же инварианту при каждом вызове тула.
/// </summary>
public static class ToolSettingEndpoints
{
    /// <summary>Регистрирует endpoint валидации tool_settings.</summary>
    public static IEndpointRouteBuilder MapToolSettingEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/v1/internal/tool-settings/validate", ValidateAsync);
        return app;
    }

    private static async Task<IResult> ValidateAsync(
        ToolSettingValidationRequest request,
        AgentPlatformDbContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        if (request.InstallationId == Guid.Empty)
        {
            return Results.BadRequest(new { error = "installation_id is required" });
        }

        if (string.IsNullOrWhiteSpace(request.ToolName))
        {
            return Results.BadRequest(new { error = "tool_name is required" });
        }

        ToolPolicy manifestPolicy;
        if (!string.IsNullOrWhiteSpace(request.ManifestPolicy))
        {
            if (!EnumTokens.TryParseDbValue<ToolPolicy>(request.ManifestPolicy, out manifestPolicy))
            {
                return Results.BadRequest(new
                {
                    error = $"unknown manifest policy '{request.ManifestPolicy}'. Allowed: read, write, destructive"
                });
            }
        }
        else
        {
            var lookup = await FindManifestToolAsync(
                context,
                request.InstallationId,
                request.ToolName,
                cancellationToken);

            if (lookup.Error is not null)
            {
                return lookup.Error;
            }

            manifestPolicy = lookup.Policy!.Value;
        }

        try
        {
            var settings = ToolSettingsValidator.ParseAndValidate(
                request.Policy,
                request.GateSettings,
                manifestPolicy,
                request.ToolName);

            return Results.Ok(new ToolSettingValidationResponse(
                request.InstallationId,
                request.ToolName,
                settings.ManifestPolicy.ToDbValue(),
                settings.PolicySource == PolicySource.TenantOverride
                    ? settings.EffectivePolicy.ToDbValue()
                    : null,
                settings.EffectivePolicy.ToDbValue(),
                settings.PolicySource.ToDbValue(),
                settings.GateMode.ToDbValue(),
                settings.Predicate is { } predicate
                    ? new GatePredicateResponse(predicate.Field, predicate.Op, predicate.Value?.DeepClone())
                    : null,
                ToRequirementToken(settings.Requirement.Kind)));
        }
        catch (PolicyEscalationException exception)
        {
            return Results.UnprocessableEntity(new
            {
                error = "policy_escalation",
                message = exception.Message,
                manifestPolicy = exception.ManifestPolicy.ToDbValue(),
                tenantPolicy = exception.TenantPolicy.ToDbValue()
            });
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
    }

    private static async Task<ManifestToolLookup> FindManifestToolAsync(
        AgentPlatformDbContext context,
        Guid installationId,
        string toolName,
        CancellationToken cancellationToken)
    {
        var registryId = await context.ConnectorInstallations
            .AsNoTracking()
            .Where(installation => installation.Id == installationId)
            .Select(installation => (Guid?)installation.RegistryId)
            .FirstOrDefaultAsync(cancellationToken);

        if (registryId is null)
        {
            return new ManifestToolLookup(
                null,
                Results.NotFound(new { error = "connector installation is not found", installationId }));
        }

        var manifestJson = await context.ConnectorRegistry
            .AsNoTracking()
            .Where(registry => registry.Id == registryId.Value)
            .Select(registry => registry.Manifest)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return new ManifestToolLookup(
                null,
                Results.UnprocessableEntity(new { error = "connector manifest is not found", installationId }));
        }

        ConnectorManifest manifest;
        try
        {
            manifest = ConnectorManifestValidator.Deserialize(manifestJson);
        }
        catch (JsonValidationException exception)
        {
            return new ManifestToolLookup(
                null,
                Results.UnprocessableEntity(new
                {
                    error = "connector manifest is invalid",
                    message = exception.Message
                }));
        }

        var tools = manifest.Tools;
        var tool = tools is null
            ? null
            : tools.FirstOrDefault(candidate => string.Equals(candidate.Name, toolName, StringComparison.Ordinal));

        if (tool is null)
        {
            return new ManifestToolLookup(
                null,
                Results.NotFound(new
                {
                    error = "tool is not declared in connector manifest",
                    toolName,
                    installationId
                }));
        }

        return new ManifestToolLookup(tool.Policy, null);
    }

    private static string ToRequirementToken(GateRequirementKind kind) => kind switch
    {
        GateRequirementKind.None => "none",
        GateRequirementKind.UserConfirmation => "user_confirmation",
        GateRequirementKind.OperatorConfirmation => "operator_confirmation",
        GateRequirementKind.Conditional => "conditional",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown gate requirement kind")
    };

    private sealed record ManifestToolLookup(ToolPolicy? Policy, IResult? Error);
}
