using AgentPlatform.Core.Contracts;

namespace AgentPlatform.Core.Abstractions;

/// <summary>
/// Источник конфигурации версии агента для Runtime (ports &amp; adapters):
/// agents + agent_versions + установленные коннекторы + tool_settings.
/// Реализация — Infrastructure (EF Core). Runtime не читает БД напрямую.
/// </summary>
public interface IAgentConfigStore
{
    /// <summary>
    /// Возвращает конфигурацию закреплённой версии агента в тенанте.
    /// null — агент/версия не найдены (или версия не принадлежит тенанту).
    /// </summary>
    /// <param name="tenantId">Тенант сессии.</param>
    /// <param name="agentId">Агент сессии.</param>
    /// <param name="agentVersion">Версия, закреплённая за сессией (version pinning).</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<AgentRuntimeConfig?> GetAsync(
        Guid tenantId,
        Guid agentId,
        int agentVersion,
        CancellationToken cancellationToken = default);
}
