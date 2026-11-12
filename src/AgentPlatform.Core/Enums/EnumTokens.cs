namespace AgentPlatform.Core.Enums;

/// <summary>
/// Преобразование enum-значений в токены БД (значения столбцов varchar в DDL) и обратно.
/// Часть значений в DDL записана в camelCase / snake_case, поэтому простое приведение
/// к нижнему регистру не подходит.
/// </summary>
public static class EnumTokens
{
    private static readonly Dictionary<(Type EnumType, string Name), string> ExplicitTokens = new()
    {
        [(typeof(ConnectorAuthType), nameof(ConnectorAuthType.ApiKey))] = "apiKey",
        [(typeof(ConnectorAuthType), nameof(ConnectorAuthType.LongLivedToken))] = "longLivedToken",
        [(typeof(ConnectorAuthType), nameof(ConnectorAuthType.OAuth2))] = "oauth2",
        [(typeof(ConnectorAuthType), nameof(ConnectorAuthType.ExternalMcp))] = "external_mcp",
        [(typeof(PolicySource), nameof(PolicySource.TenantOverride))] = "tenant_override",
        [(typeof(SessionStatus), nameof(SessionStatus.AwaitingGate))] = "awaiting_gate",
        [(typeof(ToolCallOutcome), nameof(ToolCallOutcome.GateRequired))] = "gate_required"
    };

    /// <summary>Возвращает токен значения для записи в БД.</summary>
    public static string ToDbValue(this Enum value)
        => ExplicitTokens.TryGetValue((value.GetType(), value.ToString()), out var token)
            ? token
            : value.ToString().ToLowerInvariant();

    /// <summary>
    /// Разбирает токен из БД. Неизвестное значение — <see cref="ArgumentException"/>,
    /// чтобы данные с нарушенным контрактом не проходили молча.
    /// </summary>
    public static TEnum ParseDbValue<TEnum>(string token)
        where TEnum : struct, Enum
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        foreach (var value in Enum.GetValues<TEnum>())
        {
            if (string.Equals(value.ToDbValue(), token, StringComparison.Ordinal))
            {
                return value;
            }
        }

        throw new ArgumentException(
            $"'{token}' is not a valid database token for enum {typeof(TEnum).Name}.",
            nameof(token));
    }

    /// <summary>Пытается разобрать токен из БД без исключения.</summary>
    public static bool TryParseDbValue<TEnum>(string? token, out TEnum value)
        where TEnum : struct, Enum
    {
        if (!string.IsNullOrWhiteSpace(token))
        {
            foreach (var candidate in Enum.GetValues<TEnum>())
            {
                if (string.Equals(candidate.ToDbValue(), token, StringComparison.Ordinal))
                {
                    value = candidate;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
