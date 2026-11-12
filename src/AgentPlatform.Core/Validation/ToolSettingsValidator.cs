using System.Text.Json;
using System.Text.Json.Nodes;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Json;
using AgentPlatform.Core.Policy;

namespace AgentPlatform.Core.Validation;

/// <summary>
/// Валидация конфигурации тула тенанта (tool_settings: policy + gate_settings).
/// Выполняется при сохранении настроек (Controller API) и повторно используется
/// Runtime-слоем; повышение политики (read → write) — ошибка конфигурации,
/// разрешено только понижение. Неизвестные ключи запрещены, чтобы в jsonb
/// не попадали поля, которые Policy Engine не проверяет.
/// </summary>
public static class ToolSettingsValidator
{
    /// <summary>Имя поля для сообщений об ошибках.</summary>
    public const string FieldName = "tool_settings";

    private static readonly HashSet<string> GateSettingKeys = new(StringComparer.Ordinal)
    {
        "mode",
        "predicate"
    };

    private static readonly HashSet<string> PredicateKeys = new(StringComparer.Ordinal)
    {
        "field",
        "op",
        "value"
    };

    /// <summary>
    /// Разбирает и проверяет настройки тула. Отсутствующие настройки — самый строгий
    /// режим (<see cref="GateMode.Always"/>) с манифестной политикой.
    /// </summary>
    /// <param name="policyToken">Токен политики тенанта: read | write | destructive (null — не переопределена).</param>
    /// <param name="gateSettingsJson">jsonb-документ gate_settings: <c>{"mode": "...", "predicate": {...}}</c>.</param>
    /// <param name="manifestPolicy">Политика тула из манифеста коннектора (потолок).</param>
    /// <param name="toolName">Имя тула (для сообщений об ошибках).</param>
    /// <exception cref="PolicyEscalationException">Политика тенанта выше манифестной.</exception>
    /// <exception cref="JsonValidationException">Нарушен контракт tool_settings.</exception>
    public static ResolvedToolSettings ParseAndValidate(
        string? policyToken,
        string? gateSettingsJson,
        ToolPolicy manifestPolicy,
        string toolName)
    {
        if (!Enum.IsDefined(manifestPolicy))
        {
            throw new JsonValidationException(
                FieldName,
                $"manifest policy is invalid: {(int)manifestPolicy}");
        }

        if (string.IsNullOrWhiteSpace(toolName))
        {
            throw new JsonValidationException(FieldName, "tool name is required");
        }

        var tenantPolicy = ParsePolicy(policyToken);
        var effectivePolicy = ToolPolicyRules.ResolveEffectivePolicy(manifestPolicy, tenantPolicy, toolName);
        var (gateMode, predicate) = ParseGateSettings(gateSettingsJson);

        return new ResolvedToolSettings(
            manifestPolicy,
            effectivePolicy,
            tenantPolicy.HasValue ? PolicySource.TenantOverride : PolicySource.Manifest,
            gateMode,
            predicate);
    }

    /// <summary>Проверяет настройки и возвращает действующую политику.</summary>
    public static ToolPolicy ValidateAndResolvePolicy(
        string? policyToken,
        string? gateSettingsJson,
        ToolPolicy manifestPolicy,
        string toolName)
        => ParseAndValidate(policyToken, gateSettingsJson, manifestPolicy, toolName).EffectivePolicy;

    /// <summary>
    /// Строит <see cref="ToolContext"/> для Runtime из сохранённых настроек тула
    /// (tool_settings.policy + tool_settings.gate_settings). Отсутствующие настройки
    /// не передаются как переопределения: действуют манифестная политика и дефолтный
    /// режим тенанта. Повышение политики отклоняется и здесь.
    /// </summary>
    /// <param name="installationId">Установка коннектора.</param>
    /// <param name="toolName">Имя тула из манифеста.</param>
    /// <param name="manifestPolicy">Политика из манифеста (потолок).</param>
    /// <param name="policyToken">Сохранённая политика тенанта (null — не переопределена).</param>
    /// <param name="gateSettingsJson">Сохранённый jsonb gate_settings (null/{} — настроек нет).</param>
    public static ToolContext CreateToolContext(
        Guid installationId,
        string toolName,
        ToolPolicy manifestPolicy,
        string? policyToken,
        string? gateSettingsJson)
    {
        var settings = ParseAndValidate(policyToken, gateSettingsJson, manifestPolicy, toolName);
        var policyOverridden = !string.IsNullOrWhiteSpace(policyToken);
        var gateConfigured = HasGateSettings(gateSettingsJson);

        return new ToolContext(
            installationId,
            toolName,
            settings.ManifestPolicy,
            policyOverridden ? settings.EffectivePolicy : null,
            gateConfigured ? settings.Predicate : null,
            gateConfigured ? settings.GateMode : null);
    }

    private static ToolPolicy? ParsePolicy(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        if (!EnumTokens.TryParseDbValue<ToolPolicy>(token, out var policy))
        {
            throw new JsonValidationException(
                FieldName,
                $"unknown policy '{token}'. Allowed: read, write, destructive");
        }

        return policy;
    }

    private static (GateMode Mode, GatePredicate? Predicate) ParseGateSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (GateMode.Always, null);
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new JsonValidationException(FieldName, "gate_settings must be a valid JSON document", exception);
        }

        if (root is not JsonObject settings)
        {
            throw new JsonValidationException(
                FieldName,
                "gate_settings must be a JSON object, e.g. {\"mode\":\"always\"}");
        }

        foreach (var property in settings)
        {
            if (!GateSettingKeys.Contains(property.Key))
            {
                throw new JsonValidationException(
                    FieldName,
                    $"gate_settings: unknown property '{property.Key}'. Allowed: mode, predicate");
            }
        }

        if (settings.Count == 0)
        {
            return (GateMode.Always, null);
        }

        if (!settings.TryGetPropertyValue("mode", out var modeNode))
        {
            throw new JsonValidationException(
                FieldName,
                "gate_settings.mode is required (always | conditional | never)");
        }

        if (!TryGetString(modeNode, out var modeToken)
            || !EnumTokens.TryParseDbValue<GateMode>(modeToken, out var mode))
        {
            throw new JsonValidationException(
                FieldName,
                "gate_settings.mode must be one of: always, conditional, never");
        }

        GatePredicate? predicate = null;
        if (settings.TryGetPropertyValue("predicate", out var predicateNode) && predicateNode is not null)
        {
            predicate = ParsePredicate(predicateNode);
        }

        if (mode == GateMode.Conditional && predicate is null)
        {
            throw new JsonValidationException(
                FieldName,
                "gate_settings.predicate is required for mode 'conditional'");
        }

        if (mode != GateMode.Conditional && predicate is not null)
        {
            throw new JsonValidationException(
                FieldName,
                "gate_settings.predicate is allowed for mode 'conditional' only");
        }

        return (mode, predicate);
    }

    private static GatePredicate ParsePredicate(JsonNode node)
    {
        if (node is not JsonObject predicate)
        {
            throw new JsonValidationException(FieldName, "gate_settings.predicate must be a JSON object");
        }

        foreach (var property in predicate)
        {
            if (!PredicateKeys.Contains(property.Key))
            {
                throw new JsonValidationException(
                    FieldName,
                    $"gate_settings.predicate: unknown property '{property.Key}'. Allowed: field, op, value");
            }
        }

        if (!predicate.TryGetPropertyValue("field", out var fieldNode)
            || !TryGetString(fieldNode, out var field)
            || string.IsNullOrWhiteSpace(field))
        {
            throw new JsonValidationException(
                FieldName,
                "gate_settings.predicate.field is required (tool argument name, dots allowed for nested)");
        }

        if (!predicate.TryGetPropertyValue("op", out var opNode)
            || !TryGetString(opNode, out var op)
            || string.IsNullOrWhiteSpace(op))
        {
            throw new JsonValidationException(FieldName, "gate_settings.predicate.op is required");
        }

        if (!GatePredicateEvaluator.IsSupportedOperator(op))
        {
            throw new JsonValidationException(
                FieldName,
                $"gate_settings.predicate.op '{op}' is not supported. " +
                $"Allowed: {string.Join(", ", GatePredicateEvaluator.SupportedOperators)}");
        }

        if (!predicate.TryGetPropertyValue("value", out var value))
        {
            throw new JsonValidationException(FieldName, "gate_settings.predicate.value is required");
        }

        var normalizedOp = op.ToLowerInvariant();
        if (value is null && normalizedOp is not ("eq" or "neq"))
        {
            throw new JsonValidationException(
                FieldName,
                $"gate_settings.predicate.value must not be null for operator '{normalizedOp}'");
        }

        return new GatePredicate(field.Trim(), normalizedOp, value);
    }

    private static bool HasGateSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            return JsonNode.Parse(json) is JsonObject { Count: > 0 };
        }
        catch (JsonException)
        {
            // Ошибку разбора пробросит ParseGateSettings.
            return true;
        }
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
}
