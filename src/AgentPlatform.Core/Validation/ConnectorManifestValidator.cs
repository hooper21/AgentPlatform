using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Json;

namespace AgentPlatform.Core.Validation;

/// <summary>
/// Валидация манифеста коннектора при установке (connector_registry.manifest).
/// JSON-схема — артефакт <see cref="ConnectorManifestSchema"/>; инварианты, которые
/// схема не выражает (ссылки cacheClass → cacheClasses, уникальность тулов,
/// семантика лимитов), проверяются кодом.
/// </summary>
public static partial class ConnectorManifestValidator
{
    /// <summary>Имя поля для сообщений об ошибках.</summary>
    public const string FieldName = "connector_registry.manifest";

    // Манифест — внешний контракт: неизвестные ключи запрещены, чтобы в jsonb
    // не попадали поля, которые платформа не проверяет и не соблюдает.
    private static readonly JsonSerializerOptions StrictOptions = new(PlatformJson.DbOptions)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex VersionPattern();

    /// <summary>Разбирает и проверяет jsonb-документ манифеста.</summary>
    public static ConnectorManifest ParseAndValidate(string? json)
    {
        var manifest = Deserialize(json);
        Validate(manifest);

        return manifest;
    }

    /// <summary>Проверяет jsonb-документ манифеста.</summary>
    public static void Validate(string? json) => ParseAndValidate(json);

    /// <summary>
    /// Разбирает jsonb-документ манифеста без семантической проверки.
    /// Неизвестные ключи и несоответствие типам — <see cref="JsonValidationException"/>.
    /// </summary>
    public static ConnectorManifest Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new JsonValidationException(FieldName, "manifest is required");
        }

        ConnectorManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ConnectorManifest>(json, StrictOptions);
        }
        catch (JsonException exception)
        {
            throw new JsonValidationException(
                FieldName,
                $"must match {ConnectorManifestSchema.ResourceName}",
                exception);
        }

        return manifest ?? throw new JsonValidationException(FieldName, "manifest is required");
    }

    /// <summary>Проверяет манифест как объект.</summary>
    public static void Validate(ConnectorManifest? manifest)
    {
        if (manifest is null)
        {
            throw new JsonValidationException(FieldName, "manifest is required");
        }

        RequireText(manifest.Name, "name");
        RequireText(manifest.Version, "version");
        if (!VersionPattern().IsMatch(manifest.Version))
        {
            throw new JsonValidationException(
                FieldName,
                $"'version' must be semver (major.minor.patch), got '{manifest.Version}'");
        }

        RequireText(manifest.Description, "description");

        ValidateAuth(manifest.Auth);
        ValidateCacheClasses(manifest.CacheClasses);
        ValidateTools(manifest.Tools, manifest.CacheClasses);
        ValidateLimits(manifest.Limits);
    }

    private static void ValidateAuth(ConnectorAuth? auth)
    {
        if (auth is null)
        {
            throw new JsonValidationException(FieldName, "'auth' is required");
        }

        if (!Enum.IsDefined(auth.Type))
        {
            throw new JsonValidationException(
                FieldName,
                $"'auth.type' is not a supported authentication type: {(int)auth.Type}");
        }

        if (auth.Fields is null)
        {
            return;
        }

        if (auth.Fields.Count == 0)
        {
            throw new JsonValidationException(FieldName, "'auth.fields' must not be empty when declared");
        }

        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in auth.Fields)
        {
            RequireText(field, "auth.fields");
            if (!unique.Add(field))
            {
                throw new JsonValidationException(FieldName, $"'auth.fields' contains duplicate '{field}'");
            }
        }
    }

    private static void ValidateCacheClasses(IReadOnlyDictionary<string, int>? cacheClasses)
    {
        if (cacheClasses is null)
        {
            return;
        }

        foreach (var (name, ttlSeconds) in cacheClasses)
        {
            RequireText(name, "cacheClasses key");
            if (ttlSeconds <= 0)
            {
                throw new JsonValidationException(
                    FieldName,
                    $"cache class '{name}': ttlSeconds must be > 0, got {ttlSeconds}");
            }
        }
    }

    private static void ValidateTools(
        IReadOnlyList<ConnectorToolManifest>? tools,
        IReadOnlyDictionary<string, int>? cacheClasses)
    {
        if (tools is null || tools.Count == 0)
        {
            throw new JsonValidationException(FieldName, "'tools' must contain at least one tool");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (tool is null)
            {
                throw new JsonValidationException(FieldName, "'tools' must not contain null entries");
            }

            RequireText(tool.Name, "tools[].name");
            RequireText(tool.Description, $"tool '{tool.Name}': description");
            if (!names.Add(tool.Name))
            {
                throw new JsonValidationException(FieldName, $"duplicate tool name '{tool.Name}'");
            }

            if (!Enum.IsDefined(tool.Policy))
            {
                throw new JsonValidationException(
                    FieldName,
                    $"tool '{tool.Name}': policy is not a read|write|destructive value: {(int)tool.Policy}");
            }

            ValidateInputSchema(tool);

            if (tool.CostPerCall < 0)
            {
                throw new JsonValidationException(
                    FieldName,
                    $"tool '{tool.Name}': costPerCall must not be negative");
            }

            if (tool.CacheClass is null)
            {
                continue;
            }

            RequireText(tool.CacheClass, $"tool '{tool.Name}': cacheClass");
            if (tool.Policy != ToolPolicy.Read)
            {
                throw new JsonValidationException(
                    FieldName,
                    $"tool '{tool.Name}': cacheClass is allowed for read tools only");
            }

            if (cacheClasses is null || !cacheClasses.ContainsKey(tool.CacheClass))
            {
                throw new JsonValidationException(
                    FieldName,
                    $"tool '{tool.Name}': cacheClass '{tool.CacheClass}' is not declared in cacheClasses");
            }
        }
    }

    private static void ValidateInputSchema(ConnectorToolManifest tool)
    {
        if (string.IsNullOrWhiteSpace(tool.InputSchema))
        {
            throw new JsonValidationException(
                FieldName,
                $"tool '{tool.Name}': inputSchema (JSON Schema) is required");
        }

        JsonNode? schema;
        try
        {
            schema = JsonNode.Parse(tool.InputSchema);
        }
        catch (JsonException exception)
        {
            throw new JsonValidationException(
                FieldName,
                $"tool '{tool.Name}': inputSchema must be a valid JSON Schema document",
                exception);
        }

        if (schema is not JsonObject)
        {
            throw new JsonValidationException(
                FieldName,
                $"tool '{tool.Name}': inputSchema must be a JSON object (JSON Schema)");
        }
    }

    private static void ValidateLimits(ConnectorLimits? limits)
    {
        if (limits is null)
        {
            throw new JsonValidationException(
                FieldName,
                "'limits' is required: платформа соблюдает только декларированные лимиты");
        }

        if (limits.RequestsPerWindow <= 0)
        {
            throw new JsonValidationException(
                FieldName,
                $"'limits.requestsPerWindow' must be > 0, got {limits.RequestsPerWindow}");
        }

        if (limits.WindowSeconds <= 0)
        {
            throw new JsonValidationException(
                FieldName,
                $"'limits.windowSeconds' must be > 0, got {limits.WindowSeconds}");
        }

        if (limits.WeightByEndpoint is null)
        {
            return;
        }

        foreach (var (endpoint, weight) in limits.WeightByEndpoint)
        {
            RequireText(endpoint, "limits.weightByEndpoint key");
            if (double.IsNaN(weight) || double.IsInfinity(weight) || weight <= 0)
            {
                throw new JsonValidationException(
                    FieldName,
                    $"limits.weightByEndpoint['{endpoint}'] must be a positive number, got {weight}");
            }
        }
    }

    private static void RequireText(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new JsonValidationException(
                FieldName,
                $"'{field}' is required and must be a non-empty string");
        }
    }
}
