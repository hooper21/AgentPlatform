using System.Globalization;
using System.Text.Json;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Json;

namespace AgentPlatform.Core.Validation;

/// <summary>
/// Обязательная (REQUIRED) валидация approvals.gate_payload кодом.
/// В БД v1 строгая CHECK-схема не ставится — контракт проверяется здесь.
/// Ожидаемые ключи: tool, args_masked, human_description, session_id, created_at
/// [, undo_hint].
/// </summary>
public static class GatePayloadValidator
{
    /// <summary>Имя поля для сообщений об ошибках.</summary>
    public const string FieldName = "approval.gate_payload";

    /// <summary>Проверяет payload, переданный как объект.</summary>
    public static void Validate(GatePayload? payload)
    {
        if (payload is null)
        {
            throw new JsonValidationException(FieldName, "gate_payload is required");
        }

        Validate(payload.ToJson());
    }

    /// <summary>Проверяет jsonb-документ gate_payload.</summary>
    public static void Validate(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new JsonValidationException(FieldName, "gate_payload is required");
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

            RequireNonEmptyString(root, "tool");
            RequireNonEmptyString(root, "human_description");
            RequireSessionId(root);
            RequireCreatedAt(root);

            if (!root.TryGetProperty("args_masked", out _))
            {
                throw new JsonValidationException(FieldName, "'args_masked' is required (use {} when there are no arguments)");
            }

            if (root.TryGetProperty("undo_hint", out var undoHint)
                && undoHint.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                throw new JsonValidationException(FieldName, "'undo_hint' must be a string");
            }
        }
    }

    private static void RequireNonEmptyString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new JsonValidationException(FieldName, $"'{propertyName}' is required and must be a non-empty string");
        }
    }

    private static void RequireSessionId(JsonElement root)
    {
        if (!root.TryGetProperty("session_id", out var value)
            || value.ValueKind != JsonValueKind.String
            || !Guid.TryParse(value.GetString(), out var sessionId)
            || sessionId == Guid.Empty)
        {
            throw new JsonValidationException(FieldName, "'session_id' is required and must be a non-empty UUID");
        }
    }

    private static void RequireCreatedAt(JsonElement root)
    {
        if (!root.TryGetProperty("created_at", out var value)
            || value.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _))
        {
            throw new JsonValidationException(FieldName, "'created_at' is required and must be a timestamp");
        }
    }
}
