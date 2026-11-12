using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentPlatform.Core.Json;

/// <summary>
/// Единые настройки сериализации платформы.
/// <para>
/// <see cref="DbOptions"/> — для документов, которые лежат в jsonb-полях
/// (gate_payload, rules, model_ref, enabled_tools, checkpoints.state): ключи snake_case,
/// как зафиксировано в DDL.
/// </para>
/// <para>
/// <see cref="ApiOptions"/> — для HTTP-контрактов (camelCase).
/// </para>
/// </summary>
public static class PlatformJson
{
    /// <summary>Настройки для jsonb-документов в БД (snake_case keys).</summary>
    public static readonly JsonSerializerOptions DbOptions = CreateDbOptions();

    /// <summary>Настройки для HTTP-контрактов (camelCase keys).</summary>
    public static readonly JsonSerializerOptions ApiOptions = CreateApiOptions();

    /// <summary>Сериализует документ для записи в jsonb.</summary>
    public static string SerializeDb<T>(T value)
        => JsonSerializer.Serialize(value, DbOptions);

    /// <summary>Читает документ из jsonb.</summary>
    public static T? DeserializeDb<T>(string json)
        => JsonSerializer.Deserialize<T>(json, DbOptions);

    /// <summary>Сериализует HTTP-контракт.</summary>
    public static string SerializeApi<T>(T value)
        => JsonSerializer.Serialize(value, ApiOptions);

    /// <summary>Читает HTTP-контракт.</summary>
    public static T? DeserializeApi<T>(string json)
        => JsonSerializer.Deserialize<T>(json, ApiOptions);

    private static JsonSerializerOptions CreateDbOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private static JsonSerializerOptions CreateApiOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
