using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPlatform.Core.Json;
using AgentPlatform.Core.Validation;

namespace AgentPlatform.Core.Contracts;

/// <summary>
/// Единственная точка сериализации состояния сессии в нейтральный JSON и обратно.
/// Рантайм-специфичные структуры через контракт не проходят: документ строго
/// ограничен полями <see cref="SessionState"/>, неизвестные ключи отвергаются.
/// </summary>
public static class StateContract
{
    /// <summary>Имя поля для сообщений об ошибках (checkpoints.state).</summary>
    public const string FieldName = "checkpoints.state";

    private static readonly JsonSerializerOptions Options = new(PlatformJson.DbOptions)
    {
        // Любое поле вне контракта — ошибка, а не «протащенная» структура рантайма.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>
    /// Сериализует состояние в jsonb-документ. Невалидное состояние не пишется:
    /// <see cref="Validate"/> вызывается до сериализации.
    /// </summary>
    public static string Serialize(SessionState state)
    {
        Validate(state);

        return JsonSerializer.Serialize(state, Options);
    }

    /// <summary>
    /// Читает состояние из jsonb-документа. Пустая строка — null (чекпоинта нет).
    /// Неизвестные ключи и нарушенные инварианты — <see cref="JsonValidationException"/>.
    /// </summary>
    public static SessionState? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        SessionState? state;
        try
        {
            state = JsonSerializer.Deserialize<SessionState>(json, Options);
        }
        catch (JsonException exception)
        {
            throw new JsonValidationException(
                FieldName,
                "must be a valid session state document (see StateContract)",
                exception);
        }

        if (state is null)
        {
            throw new JsonValidationException(FieldName, "state is required");
        }

        Validate(state);

        return state;
    }

    /// <summary>Читает состояние, требуя наличие чекпоинта.</summary>
    public static SessionState DeserializeRequired(string? json)
        => Deserialize(json) ?? throw new JsonValidationException(FieldName, "state is required");

    /// <summary>Пытается прочитать состояние без исключения.</summary>
    public static bool TryDeserialize(string? json, out SessionState? state)
    {
        try
        {
            state = Deserialize(json);
            return state is not null;
        }
        catch (JsonValidationException)
        {
            state = null;
            return false;
        }
    }

    /// <summary>
    /// Проверяет инварианты состояния: поддерживаемая версия, непустой контент
    /// сообщений, согласованность pending_gate и неотрицательные счётчики.
    /// Бросает <see cref="JsonValidationException"/> при нарушении.
    /// </summary>
    public static void Validate(SessionState? state)
    {
        if (state is null)
        {
            throw new JsonValidationException(FieldName, "state is required");
        }

        if (state.Version != SessionState.CurrentVersion)
        {
            throw new JsonValidationException(
                FieldName,
                $"unsupported state version {state.Version}, expected {SessionState.CurrentVersion}");
        }

        if (state.Messages is null)
        {
            throw new JsonValidationException(FieldName, "'messages' is required (use [] when empty)");
        }

        for (var index = 0; index < state.Messages.Count; index++)
        {
            ValidateMessage(state.Messages[index], index);
        }

        if (state.PendingGate is { } pendingGate)
        {
            if (pendingGate.ApprovalId == Guid.Empty)
            {
                throw new JsonValidationException(FieldName, "pending_gate.approval_id is required");
            }

            GatePayloadValidator.Validate(pendingGate.Payload);
        }

        ValidateCounters(state.Counters);
    }

    private static void ValidateMessage(StateMessage? message, int index)
    {
        if (message is null)
        {
            throw new JsonValidationException(FieldName, $"messages[{index}] is required");
        }

        if (!Enum.IsDefined(message.Role))
        {
            throw new JsonValidationException(
                FieldName,
                $"messages[{index}].role is not a known role: {(int)message.Role}");
        }

        if (string.IsNullOrWhiteSpace(message.Content))
        {
            throw new JsonValidationException(FieldName, $"messages[{index}].content must not be empty");
        }

        if (message.Timestamp == default)
        {
            throw new JsonValidationException(FieldName, $"messages[{index}].timestamp is required");
        }

        if (message.ToolCalls is null)
        {
            return;
        }

        for (var toolIndex = 0; toolIndex < message.ToolCalls.Count; toolIndex++)
        {
            var toolCall = message.ToolCalls[toolIndex];
            if (toolCall is null || string.IsNullOrWhiteSpace(toolCall.Name))
            {
                throw new JsonValidationException(
                    FieldName,
                    $"messages[{index}].tool_calls[{toolIndex}].name is required");
            }

            if (toolCall.LatencyMs < 0)
            {
                throw new JsonValidationException(
                    FieldName,
                    $"messages[{index}].tool_calls[{toolIndex}].latency_ms must not be negative");
            }
        }
    }

    private static void ValidateCounters(SessionCounters? counters)
    {
        if (counters is null)
        {
            throw new JsonValidationException(FieldName, "counters is required");
        }

        if (counters.Turns < 0
            || counters.ToolCalls < 0
            || counters.FailedTurns < 0
            || counters.TokensIn < 0
            || counters.TokensOut < 0)
        {
            throw new JsonValidationException(FieldName, "counters must not be negative");
        }
    }
}
