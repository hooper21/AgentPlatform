using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentPlatform.Runtime.Orchestration;

/// <summary>
/// Работа с аргументами тулов: модель возвращает их словарём, в чекпоинт и в
/// Policy Engine они попадают как JSON-строка/узел.
/// </summary>
internal static class ToolJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        // Аргументы в чекпоинте должны читаться человеком (кириллица без \uXXXX).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Сериализует аргументы вызова тула из ответа модели.</summary>
    public static string FromArguments(IDictionary<string, object?>? arguments)
        => arguments is null || arguments.Count == 0
            ? "{}"
            : JsonSerializer.Serialize(arguments, Options);

    /// <summary>Читает аргументы как узел JSON (null — аргументов нет).</summary>
    public static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            // Некорректные аргументы трактуются Policy Engine как срабатывание гейта (fail-closed).
            return null;
        }
    }

    /// <summary>Сериализует аргументы обратно в JSON-строку (resume из gate payload).</summary>
    public static string ToJsonString(JsonNode? node) => node is null ? "{}" : node.ToJsonString(Options);
}

