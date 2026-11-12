using System.Text.Json;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Json;

namespace AgentPlatform.Core.Validation;

/// <summary>
/// Обязательная (REQUIRED) валидация agent_versions.rules кодом.
/// Схема v1: JSON-объект с ключами
/// qualification (string), qualification_questions (string[]), restrictions (string[]),
/// ask_before_write (bool), notes (string). Неизвестные ключи запрещены,
/// чтобы правила не превращались в свалку невалидируемых данных.
/// </summary>
public static class AgentRulesValidator
{
    /// <summary>Имя поля для сообщений об ошибках.</summary>
    public const string FieldName = "agents.rules";

    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "qualification",
        "qualification_questions",
        "restrictions",
        "ask_before_write",
        "notes"
    };

    /// <summary>Проверяет правила, переданные как объект.</summary>
    public static void Validate(AgentRules? rules)
    {
        if (rules is null)
        {
            throw new JsonValidationException(FieldName, "rules is required (use {} for empty rules)");
        }

        Validate(PlatformJson.SerializeDb(rules));
    }

    /// <summary>Проверяет jsonb-документ rules.</summary>
    public static void Validate(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new JsonValidationException(FieldName, "rules is required (use {} for empty rules)");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new JsonValidationException(FieldName, "must be a valid JSON document", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonValidationException(FieldName, "must be a JSON object");
            }

            foreach (var property in root.EnumerateObject())
            {
                if (!AllowedKeys.Contains(property.Name))
                {
                    throw new JsonValidationException(
                        FieldName,
                        $"unknown property '{property.Name}'. Allowed: {string.Join(", ", AllowedKeys)}");
                }

                switch (property.Name)
                {
                    case "qualification":
                    case "notes":
                        RequireString(property);
                        break;
                    case "ask_before_write":
                        if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        {
                            throw new JsonValidationException(FieldName, "'ask_before_write' must be a boolean");
                        }

                        break;
                    default:
                        RequireStringArray(property);
                        break;
                }
            }
        }
    }

    private static void RequireString(JsonProperty property)
    {
        if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            throw new JsonValidationException(FieldName, $"'{property.Name}' must be a string");
        }
    }

    private static void RequireStringArray(JsonProperty property)
    {
        if (property.Value.ValueKind != JsonValueKind.Array)
        {
            throw new JsonValidationException(FieldName, $"'{property.Name}' must be an array of strings");
        }

        foreach (var item in property.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new JsonValidationException(FieldName, $"'{property.Name}' must contain only strings");
            }
        }
    }
}
