using AgentPlatform.Core.Contracts;

namespace AgentPlatform.Runtime;

/// <summary>
/// Кеш конфигурации агента (TTL 60 с). Инвалидируется при изменении конфигурации
/// (сохранение версии агента, настроек тулов, установка/отключение коннектора).
/// </summary>
public interface IAgentConfigCache
{
    /// <summary>Читает конфигурацию закреплённой версии из кеша (null — промах).</summary>
    Task<AgentRuntimeConfig?> GetAsync(
        Guid tenantId,
        Guid agentId,
        int agentVersion,
        CancellationToken cancellationToken = default);

    /// <summary>Кладёт конфигурацию в кеш с TTL.</summary>
    Task SetAsync(AgentRuntimeConfig config, CancellationToken cancellationToken = default);

    /// <summary>Инвалидирует все версии агента (вызывается после изменения конфигурации).</summary>
    Task InvalidateAsync(Guid tenantId, Guid agentId, CancellationToken cancellationToken = default);
}
