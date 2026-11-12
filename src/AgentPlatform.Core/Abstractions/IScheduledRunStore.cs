using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;

namespace AgentPlatform.Core.Abstractions;

/// <summary>
/// Хранилище расписаний планировщика (таблица scheduled_runs).
/// <para>
/// Чтение и изменение ведутся в разрезе tenant_id: Scheduler получает тенантов
/// из глобальной таблицы tenants и дальше работает только с расписаниями текущего
/// тенанта, а Controller API принимает tenant_id из маршрута.
/// </para>
/// </summary>
public interface IScheduledRunStore
{
    /// <summary>Расписания тенанта.</summary>
    Task<IReadOnlyList<ScheduledRunInfo>> ListAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Расписание тенанта или null (запись другого тенанта не видна).</summary>
    Task<ScheduledRunInfo?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>Создаёт расписание.</summary>
    Task<ScheduledRunInfo> CreateAsync(
        Guid tenantId,
        Guid agentId,
        string cronExpression,
        bool enabled,
        CancellationToken cancellationToken = default);

    /// <summary>Изменяет расписание; null — расписание не найдено у тенанта.</summary>
    Task<ScheduledRunInfo?> UpdateAsync(
        Guid tenantId,
        Guid id,
        Guid agentId,
        string cronExpression,
        bool enabled,
        CancellationToken cancellationToken = default);

    /// <summary>Удаляет расписание; false — расписание не найдено у тенанта.</summary>
    Task<bool> DeleteAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>Фиксирует итог запуска: last_run_at и last_run_status (ok | error).</summary>
    Task MarkRunAsync(
        Guid tenantId,
        Guid id,
        DateTimeOffset lastRunAt,
        ScheduledRunStatus status,
        CancellationToken cancellationToken = default);
}
