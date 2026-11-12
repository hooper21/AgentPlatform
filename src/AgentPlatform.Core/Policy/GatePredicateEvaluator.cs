using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentPlatform.Core.Contracts;

namespace AgentPlatform.Core.Policy;

/// <summary>
/// Серверное вычисление conditional-предиката (<see cref="GatePredicate"/>,
/// tool_settings.gate_settings.predicate) на аргументах вызова тула.
/// Поддерживается фиксированный набор операторов; код предиката не исполняется
/// и промпт агента на решение не влияет.
/// </summary>
public static class GatePredicateEvaluator
{
    private static readonly string[] OperatorList = ["eq", "neq", "gt", "gte", "lt", "lte", "in", "contains"];

    /// <summary>Поддерживаемые операторы предиката (токены конфигурации).</summary>
    public static IReadOnlyList<string> SupportedOperators => OperatorList;

    /// <summary>true, если оператор поддерживается Policy Engine.</summary>
    public static bool IsSupportedOperator(string? op)
        => !string.IsNullOrWhiteSpace(op)
           && Array.Exists(OperatorList, candidate => string.Equals(candidate, op, StringComparison.OrdinalIgnoreCase));

    /// <summary>Проверяет, что предикат пригоден для вычисления.</summary>
    public static bool IsValid(GatePredicate? predicate)
        => predicate is not null
           && !string.IsNullOrWhiteSpace(predicate.Field)
           && IsSupportedOperator(predicate.Op);

    /// <summary>
    /// Вычисляет предикат на аргументах тула. Отсутствующее в аргументах поле —
    /// предикат не выполнен (false). Некорректный предикат или аргументы —
    /// <see cref="GatePredicateException"/>: вызывающая сторона обязана трактовать
    /// это как срабатывание гейта (fail-closed).
    /// </summary>
    /// <param name="predicate">Предикат режима conditional.</param>
    /// <param name="arguments">JSON-аргументы вызова тула.</param>
    public static bool Evaluate(GatePredicate predicate, JsonNode? arguments)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        if (!IsValid(predicate))
        {
            throw new GatePredicateException(
                $"predicate is invalid: 'field' and a supported 'op' are required " +
                $"(allowed ops: {string.Join(", ", OperatorList)})");
        }

        if (arguments is null)
        {
            return false;
        }

        if (arguments is not JsonObject argumentsObject)
        {
            throw new GatePredicateException("tool arguments must be a JSON object");
        }

        if (!TryResolveField(argumentsObject, predicate.Field, out var actual))
        {
            return false;
        }

        var op = predicate.Op.ToLowerInvariant();
        var expected = predicate.Value;

        return op switch
        {
            "eq" => AreEqual(actual, expected),
            "neq" => !AreEqual(actual, expected),
            "gt" => Compare(actual, expected, op) > 0,
            "gte" => Compare(actual, expected, op) >= 0,
            "lt" => Compare(actual, expected, op) < 0,
            "lte" => Compare(actual, expected, op) <= 0,
            "in" => IsIn(actual, expected),
            "contains" => Contains(actual, expected, predicate.Field),
            _ => throw new GatePredicateException($"unsupported predicate operator '{predicate.Op}'")
        };
    }

    private static bool TryResolveField(JsonObject arguments, string field, out JsonNode? value)
    {
        var segments = field.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            value = null;
            return false;
        }

        JsonNode? current = arguments;
        foreach (var segment in segments)
        {
            if (current is not JsonObject currentObject || !TryGetProperty(currentObject, segment, out current))
            {
                value = null;
                return false;
            }
        }

        value = current;
        return true;
    }

    private static bool TryGetProperty(JsonObject source, string name, out JsonNode? value)
    {
        if (source.TryGetPropertyValue(name, out value))
        {
            return true;
        }

        foreach (var (key, candidate) in source)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = candidate;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool AreEqual(JsonNode? actual, JsonNode? expected)
    {
        if (actual is null || expected is null)
        {
            return actual is null && expected is null;
        }

        if (TryGetDecimal(actual, out var actualNumber) && TryGetDecimal(expected, out var expectedNumber))
        {
            return actualNumber == expectedNumber;
        }

        if (TryGetString(actual, out var actualText) && TryGetString(expected, out var expectedText))
        {
            return string.Equals(actualText, expectedText, StringComparison.Ordinal);
        }

        if (TryGetBoolean(actual, out var actualBoolean) && TryGetBoolean(expected, out var expectedBoolean))
        {
            return actualBoolean == expectedBoolean;
        }

        return JsonNode.DeepEquals(actual, expected);
    }

    private static int Compare(JsonNode? actual, JsonNode? expected, string op)
    {
        if (!TryGetDecimal(actual, out var actualNumber) || !TryGetDecimal(expected, out var expectedNumber))
        {
            throw new GatePredicateException($"operator '{op}' requires numeric values");
        }

        return actualNumber.CompareTo(expectedNumber);
    }

    private static bool IsIn(JsonNode? actual, JsonNode? expected)
    {
        if (expected is not JsonArray expectedItems)
        {
            throw new GatePredicateException("operator 'in' requires an array value");
        }

        foreach (var item in expectedItems)
        {
            if (AreEqual(actual, item))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Contains(JsonNode? actual, JsonNode? expected, string field)
    {
        if (actual is JsonArray actualItems)
        {
            foreach (var item in actualItems)
            {
                if (AreEqual(item, expected))
                {
                    return true;
                }
            }

            return false;
        }

        if (TryGetString(actual, out var actualText) && TryGetString(expected, out var expectedText))
        {
            return actualText.Contains(expectedText, StringComparison.Ordinal);
        }

        throw new GatePredicateException(
            $"operator 'contains' requires a string or an array at field '{field}'");
    }

    private static bool TryGetString(JsonNode? node, out string value)
    {
        value = string.Empty;

        if (node is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var text) || text is null)
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool TryGetBoolean(JsonNode? node, out bool value)
    {
        value = false;

        return node is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out value);
    }

    private static bool TryGetDecimal(JsonNode? node, out decimal value)
    {
        value = 0m;

        if (node is not JsonValue jsonValue)
        {
            return false;
        }

        if (jsonValue.TryGetValue<JsonElement>(out var element))
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out value))
            {
                return true;
            }
        }
        else if (TryGetNumericPrimitive(jsonValue, out value))
        {
            return true;
        }

        return TryGetString(node, out var text)
               && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetNumericPrimitive(JsonValue value, out decimal result)
    {
        result = 0m;

        if (value.TryGetValue<long>(out var longValue))
        {
            result = longValue;
            return true;
        }

        if (value.TryGetValue<int>(out var intValue))
        {
            result = intValue;
            return true;
        }

        if (value.TryGetValue<short>(out var shortValue))
        {
            result = shortValue;
            return true;
        }

        if (value.TryGetValue<sbyte>(out var sbyteValue))
        {
            result = sbyteValue;
            return true;
        }

        if (value.TryGetValue<ulong>(out var ulongValue))
        {
            result = ulongValue;
            return true;
        }

        if (value.TryGetValue<uint>(out var uintValue))
        {
            result = uintValue;
            return true;
        }

        if (value.TryGetValue<ushort>(out var ushortValue))
        {
            result = ushortValue;
            return true;
        }

        if (value.TryGetValue<byte>(out var byteValue))
        {
            result = byteValue;
            return true;
        }

        if (value.TryGetValue<decimal>(out var decimalValue))
        {
            result = decimalValue;
            return true;
        }

        if (value.TryGetValue<double>(out var doubleValue)
            && !double.IsNaN(doubleValue)
            && !double.IsInfinity(doubleValue))
        {
            result = (decimal)doubleValue;
            return true;
        }

        if (value.TryGetValue<float>(out var floatValue)
            && !float.IsNaN(floatValue)
            && !float.IsInfinity(floatValue))
        {
            result = (decimal)floatValue;
            return true;
        }

        return false;
    }
}
