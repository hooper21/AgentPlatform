using System.Text.Json.Nodes;

namespace AgentPlatform.Controller.Contracts;

/// <summary>
/// Запрос валидации настроек тула тенанта (tool_settings) перед сохранением.
/// Политика и режим гейта передаются токенами БД: read | write | destructive,
/// always | conditional | never. Манифестная политика либо задаётся явно,
/// либо берётся из connector_registry.manifest установки.
/// </summary>
/// <param name="InstallationId">Установка коннектора (connector_installations.id).</param>
/// <param name="ToolName">Имя тула из манифеста.</param>
/// <param name="Policy">Политика тенанта (null — из манифеста, без переопределения).</param>
/// <param name="GateSettings">jsonb gate_settings: <c>{"mode": "...", "predicate": {...}}</c>.</param>
/// <param name="ManifestPolicy">Манифестная политика (null — прочитать из манифеста в БД).</param>
public sealed record ToolSettingValidationRequest(
    Guid InstallationId,
    string ToolName,
    string? Policy = null,
    string? GateSettings = null,
    string? ManifestPolicy = null);

/// <summary>Результат валидации: действующая политика и требование гейта.</summary>
/// <param name="InstallationId">Установка коннектора.</param>
/// <param name="ToolName">Имя тула.</param>
/// <param name="ManifestPolicy">Политика из манифеста (потолок).</param>
/// <param name="TenantPolicy">Политика тенанта (null — не переопределена).</param>
/// <param name="EffectivePolicy">Действующая политика после проверки понижения.</param>
/// <param name="PolicySource">manifest | tenant_override.</param>
/// <param name="GateMode">always | conditional | never.</param>
/// <param name="Predicate">Предикат режима conditional (если задан).</param>
/// <param name="GateRequirement">none | user_confirmation | operator_confirmation | conditional.</param>
public sealed record ToolSettingValidationResponse(
    Guid InstallationId,
    string ToolName,
    string ManifestPolicy,
    string? TenantPolicy,
    string EffectivePolicy,
    string PolicySource,
    string GateMode,
    GatePredicateResponse? Predicate,
    string GateRequirement);

/// <summary>Предикат conditional-гейта в ответе API.</summary>
public sealed record GatePredicateResponse(string Field, string Op, JsonNode? Value);
