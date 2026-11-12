using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Dto;

namespace AgentPlatform.Core.Abstractions;

/// <summary>
/// Policy Engine: серверное определение необходимости HITL-гейта. Политика берётся
/// из манифеста тула и настроек тенанта (tool_settings), а не из промпта агента.
/// Повышение политики конфигурацией запрещено, понижение разрешено.
/// </summary>
public interface IPolicyEngine
{
    /// <summary>
    /// Возвращает требование к гейту для вызова тула:
    /// <see cref="GateRequirement.None"/> | <see cref="GateRequirement.UserConfirmation"/> |
    /// <see cref="GateRequirement.OperatorConfirmation"/> |
    /// <see cref="GateRequirement.Conditional"/>.
    /// </summary>
    /// <param name="tool">Тул: манифестная политика, переопределение тенанта, условие.</param>
    /// <param name="tenant">Настройки тенанта по умолчанию (самый строгий режим, если настроек нет).</param>
    /// <param name="session">Контекст сессии (канал, визитер, статус) для правил уровня сессии.</param>
    GateRequirement GetGateRequirement(
        ToolContext tool,
        TenantPolicyContext tenant,
        SessionContext session);
}
