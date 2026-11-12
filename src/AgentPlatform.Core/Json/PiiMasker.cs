using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentPlatform.Core.Json;

/// <summary>
/// Маскирование ПДн в аргументах тулов перед записью в approvals/карточку/логи.
/// Маска ставится на значение, если ключ похож на персональные данные
/// (email, телефон, паспорт и т.п.); остальные аргументы сохраняются как есть,
/// чтобы подтверждающий видел суть действия.
/// <para>
/// Платформа маскирует данные на входе, поэтому в карточку и в approvals.gate_payload
/// попадает только результат <see cref="Mask"/>. Полные аргументы хранятся в чекпоинте.
/// </para>
/// </summary>
public static class PiiMasker
{
    /// <summary>Значение-замена для замаскированного поля.</summary>
    public const string MaskedValue = "***";

    private static readonly string[] SensitiveKeyParts =
    [
        "password",
        "passwd",
        "secret",
        "token",
        "apikey",
        "api_key",
        "email",
        "phone",
        "passport",
        "snils",
        "card",
        "address",
        "birth"
    ];

    /// <summary>
    /// Возвращает копию JSON-аргументов, в которой значения чувствительных полей
    /// заменены маской. Исходный узел не изменяется.
    /// </summary>
    public static JsonNode? Mask(JsonNode? value) => MaskNode(value);

    /// <summary>
    /// Маскирует JSON-строку аргументов тула. Некорректный JSON не раскрывается:
    /// возвращается пустой объект.
    /// </summary>
    public static string MaskJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return "{}";
        }

        return Mask(node)?.ToJsonString() ?? "{}";
    }

    private static JsonNode? MaskNode(JsonNode? node) => node switch
    {
        null => null,
        JsonObject jsonObject => MaskObject(jsonObject),
        JsonArray jsonArray => MaskArray(jsonArray),
        _ => node.DeepClone()
    };

    private static JsonObject MaskObject(JsonObject source)
    {
        var masked = new JsonObject();

        foreach (var (key, value) in source)
        {
            masked[key] = IsSensitiveKey(key) ? JsonValue.Create(MaskedValue) : MaskNode(value);
        }

        return masked;
    }

    private static JsonArray MaskArray(JsonArray source)
    {
        var masked = new JsonArray();

        foreach (var item in source)
        {
            masked.Add(MaskNode(item));
        }

        return masked;
    }

    private static bool IsSensitiveKey(string key)
        => SensitiveKeyParts.Any(part => key.Contains(part, StringComparison.OrdinalIgnoreCase));
}
