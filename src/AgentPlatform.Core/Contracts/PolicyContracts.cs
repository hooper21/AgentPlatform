using System.Text.Json.Nodes;
using AgentPlatform.Core.Enums;

namespace AgentPlatform.Core.Contracts;

/// <summary>
/// Вид требования к HITL-гейту. Значения — закрытый набор Policy Engine:
/// проверка гейта не может «придумать» режим вне контракта.
/// </summary>
public enum GateRequirementKind
{
    /// <summary>Подтверждение не требуется (read-тул).</summary>
    None = 0,

    /// <summary>Подтверждение визитера в канале.</summary>
    UserConfirmation = 1,

    /// <summary>Подтверждение оператора тенанта (кабинет/панель оператора).</summary>
    OperatorConfirmation = 2,

    /// <summary>Подтверждение требуется, только если предикат выполняется на аргументах.</summary>
    Conditional = 3
}

/// <summary>
/// Требование Policy Engine: <c>None | UserConfirmation | OperatorConfirmation | Conditional(predicate)</c>.
/// Создаётся только фабриками, чтобы режим и предикат были согласованы.
/// </summary>
/// <param name="Kind">Вид требования.</param>
/// <param name="Predicate">Предикат режима <see cref="GateRequirementKind.Conditional"/>.</param>
public sealed record GateRequirement(GateRequirementKind Kind, GatePredicate? Predicate = null)
{
    /// <summary>Подтверждение не требуется.</summary>
    public static GateRequirement None { get; } = new(GateRequirementKind.None);

    /// <summary>Требуется подтверждение визитера.</summary>
    public static GateRequirement UserConfirmation { get; } = new(GateRequirementKind.UserConfirmation);

    /// <summary>Требуется подтверждение оператора.</summary>
    public static GateRequirement OperatorConfirmation { get; } = new(GateRequirementKind.OperatorConfirmation);

    /// <summary>Требуется подтверждение по условию на аргументах вызова.</summary>
    public static GateRequirement Conditional(GatePredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        return new GateRequirement(GateRequirementKind.Conditional, predicate);
    }

    /// <summary>true, если подтверждение требуется (любой вид, кроме None).</summary>
    public bool RequiresConfirmation => Kind != GateRequirementKind.None;
}

/// <summary>
/// Предикат режима conditional (tool_settings.gate_settings.predicate):
/// <c>{"field": "amount", "op": "gt", "value": 50000}</c>.
/// Применяется к аргументам тула серверно; состав операторов фиксирует задача 1.3.
/// </summary>
/// <param name="Field">Поле аргументов тула.</param>
/// <param name="Op">Оператор сравнения (gt, gte, lt, lte, eq, neq, …).</param>
/// <param name="Value">Значение сравнения.</param>
public sealed record GatePredicate(string Field, string Op, JsonNode? Value = null);

/// <summary>
/// Тул, для которого Policy Engine вычисляет требование гейта.
/// </summary>
/// <param name="InstallationId">Установка коннектора (connector_installations.id).</param>
/// <param name="ToolName">Имя тула из манифеста.</param>
/// <param name="ManifestPolicy">Политика из манифеста: read | write | destructive.</param>
/// <param name="TenantPolicy">Политика из tool_settings тенанта (понижение допустимо, повышение — нет).</param>
/// <param name="ConditionalPredicate">Условие режима conditional из tool_settings (если задано).</param>
public sealed record ToolContext(
    Guid InstallationId,
    string ToolName,
    ToolPolicy ManifestPolicy,
    ToolPolicy? TenantPolicy = null,
    GatePredicate? ConditionalPredicate = null);

/// <summary>
/// Настройки тенанта, влияющие на гейт. Если настроек нет — действует самый
/// строгий режим (<see cref="GateMode.Always"/>).
/// </summary>
/// <param name="TenantId">Тенант.</param>
/// <param name="DefaultGateMode">Режим гейта по умолчанию для тулов без tool_settings.</param>
public sealed record TenantPolicyContext(
    Guid TenantId,
    GateMode DefaultGateMode = GateMode.Always);
