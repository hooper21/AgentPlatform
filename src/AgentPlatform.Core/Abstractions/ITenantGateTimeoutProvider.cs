namespace AgentPlatform.Core.Abstractions;

/// <summary>
/// Таймаут ожидания решения по HITL-гейту: настройка тенанта.
/// Значение по умолчанию для тенанта без настройки — 24 часа.
/// </summary>
public interface ITenantGateTimeoutProvider
{
    /// <summary>
    /// Срок жизни заявки approvals для тенанта. Должен быть положительным.
    /// </summary>
    TimeSpan GetGateTimeout(Guid tenantId);
}
