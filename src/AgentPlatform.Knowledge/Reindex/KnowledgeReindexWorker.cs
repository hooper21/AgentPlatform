using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Scheduling;
using AgentPlatform.Infrastructure.Persistence.Entities;
using AgentPlatform.Infrastructure.Tenancy;
using AgentPlatform.Knowledge.Configuration;
using AgentPlatform.Knowledge.Contracts;
using AgentPlatform.Knowledge.Ingest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentPlatform.Knowledge.Reindex;

/// <summary>
/// Переиндексация знаний по расписанию. Cron хранится в
/// knowledge_sources.index_meta (reindex_cron) и интерпретируется тем же
/// контрактом <see cref="CronSchedule"/> (UTC, 5 полей), что и планировщик
/// платформы. Таблица scheduled_runs не используется: у неё обязательный
/// agent_id (FK на agents), а Knowledge Service независим от Runtime/агентов.
/// </summary>
public sealed class KnowledgeReindexWorker(
    IServiceScopeFactory scopeFactory,
    KnowledgeOptions options,
    TimeProvider timeProvider,
    ILogger<KnowledgeReindexWorker> logger) : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    private readonly KnowledgeOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<KnowledgeReindexWorker> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Knowledge reindex scheduler started: poll interval {Interval}",
            _options.ReindexPollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var reindexed = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                if (reindexed > 0)
                {
                    _logger.LogInformation("Knowledge reindex tick: {Count} sources reindexed", reindexed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Ошибка тика не должна останавливать процесс.
                _logger.LogError(exception, "Knowledge reindex tick failed");
            }

            try
            {
                await Task.Delay(_options.ReindexPollInterval, _timeProvider, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Один проход: созревшие расписания переиндексируются. Возвращает число прогонов.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var reindexed = 0;

        IReadOnlyList<Tenant> tenants;
        using (var tenantScope = _scopeFactory.CreateScope())
        {
            var repository = tenantScope.ServiceProvider.GetRequiredService<IGlobalRepository<Tenant>>();
            tenants = await repository.ListAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var tenant in tenants.Where(candidate => string.Equals(
            candidate.Status,
            TenantStatus.Active.ToDbValue(),
            StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var scope = _scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenant.Id);

            var sources = scope.ServiceProvider
                .GetRequiredService<ITenantScopedRepository<KnowledgeSource>>();

            var list = await sources.ListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var source in list)
            {
                var meta = KnowledgeIndexMeta.Parse(source.IndexMeta);
                if (string.IsNullOrWhiteSpace(meta.ReindexCron))
                {
                    continue;
                }

                if (!CronSchedule.TryParse(meta.ReindexCron, out var schedule, out var error))
                {
                    _logger.LogWarning(
                        "Knowledge source {SourceId} has invalid reindex_cron '{Cron}': {Error}",
                        source.Id,
                        meta.ReindexCron,
                        error);
                    continue;
                }

                var anchor = meta.LastIndexedAt ?? meta.AttemptedAt ?? source.UpdatedAt;
                if (!schedule.IsDue(anchor, now))
                {
                    continue;
                }

                var ingest = scope.ServiceProvider.GetRequiredService<KnowledgeIngestService>();
                var updated = await ingest.ReindexAsync(source.Id, cancellationToken).ConfigureAwait(false);
                if (updated is not null)
                {
                    reindexed++;
                }
            }
        }

        return reindexed;
    }
}
