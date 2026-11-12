namespace AgentPlatform.Core.Contracts;

/// <summary>
/// JSON Schema манифеста коннектора — артефакт платформы, встроенный в сборку Core
/// (<c>Contracts/connector-manifest.schema.json</c>, draft 2020-12).
/// Используется каталогом коннекторов и install-flow: манифест публикуется вместе
/// со схемой, по которой он проверяется (<see cref="Validation.ConnectorManifestValidator"/>).
/// </summary>
public static class ConnectorManifestSchema
{
    /// <summary>Логическое имя embedded resource со схемой.</summary>
    public const string ResourceName = "AgentPlatform.Core.Contracts.connector-manifest.schema.json";

    private static readonly Lazy<string> SchemaJson =
        new(LoadSchema, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Текст JSON Schema манифеста.</summary>
    public static string Json => SchemaJson.Value;

    private static string LoadSchema()
    {
        var assembly = typeof(ConnectorManifestSchema).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' is missing: schema artifact was not packed into AgentPlatform.Core.");
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
