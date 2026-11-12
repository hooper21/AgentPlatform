namespace AgentPlatform.Core.Json;

/// <summary>
/// Нарушение контракта jsonb-документа. Для approval.gate_payload и agents.rules
/// валидация обязательна (REQUIRED) — см. <see cref="Validation.GatePayloadValidator"/>
/// и <see cref="Validation.AgentRulesValidator"/>.
/// </summary>
public sealed class JsonValidationException : Exception
{
    public JsonValidationException(string fieldName, string message)
        : base($"{fieldName}: {message}")
    {
        FieldName = fieldName;
    }

    public JsonValidationException(string fieldName, string message, Exception innerException)
        : base($"{fieldName}: {message}", innerException)
    {
        FieldName = fieldName;
    }

    /// <summary>Имя поля/контракта, на котором упала валидация.</summary>
    public string FieldName { get; }
}
