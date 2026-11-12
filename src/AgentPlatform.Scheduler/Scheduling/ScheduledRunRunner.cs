using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Scheduling;
using AgentPlatform.Infrastructure.Persistence.Entities;
using AgentPlatform.Infrastructure.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPlatform.Scheduler.Scheduling;

/// <summary>
/// Один тик планировщика: перебирает активных тенантов, читает scheduled_runs
/// (только своего тенанта) и запускает те расписания, чей cron уже наступил.
/// <para>
/// Тик не пересекается сам с собой: worker вызывает его последовательно, поэтому
/// один и тот же агент не запускается дважды. Несколько экземпляров Scheduler
/// одновременно не координируются — для v1 запускается один экземпляр.
/// </para>
/// </summary>
public sealed class ScheduledRunRunner(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ScheduledRunRunner> logger)
{
    private readonly IServiceScopeFactory _scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<ScheduledRunRunner> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Выполняет один проход по расписаниям; возвращает число запусков.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var launched = 0;

        // tenants — глобальная таблица платформы: Scheduler обязан видеть всех тенантов.
        using var tenantListScope = _scopeFactory.CreateScope();
        var tenants = tenantListScope.ServiceProvider.GetRequiredService<IGlobalRepository<Tenant>>();
        var activeTenants = (await tenants.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(tenant => string.Equals(
                tenant.Status,
                TenantStatus.Active.ToDbValue(),
                StringComparison.Ordinal))
            .ToList();

        foreach (var tenant in activeTenants)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Отдельный scope на чтение расписаний тенанта.
            IReadOnlyList<ScheduledRunInfo> runs;
            using (var listScope = _scopeFactory.CreateScope())
            {
                listScope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenant.Id);
                var store = listScope.ServiceProvider.GetRequiredService<IScheduledRunStore>();
                runs = await store.ListAsync(tenant.Id, cancellationToken).ConfigureAwait(false);
            }

            foreach (var run in runs)
            {
                if (!run.Enabled)
                {
                    continue;
                }

                if (!CronSchedule.TryParse(run.CronExpression, out var schedule, out var cronError))
                {
                    _logger.LogWarning(
                        "Scheduled run {ScheduledRunId} has invalid cron '{CronExpression}': {Error}",
                        run.Id,
                        run.CronExpression,
                        cronError);
                    continue;
                }

                // Точка отсчёта: последний запуск или последнее изменение расписания.
                var anchor = run.LastRunAt ?? run.UpdatedAt;
                if (!schedule.IsDue(anchor, now))
                {
                    continue;
                }

                // Отдельный scope на запуск: ошибка одного расписания не влияет на следующее.
                using var runScope = _scopeFactory.CreateScope();
                runScope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenant.Id);
                var executor = runScope.ServiceProvider.GetRequiredService<ScheduledRunExecutor>();
                var execution = await executor.ExecuteAsync(run, cancellationToken).ConfigureAwait(false);

                launched++;

                if (!execution.Succeeded)
                {
                    _logger.LogWarning(
                        "Scheduled run {ScheduledRunId} failed: {Error}",
                        run.Id,
                        execution.Error);
                }
            }
        }

        return launched;
    }
}
