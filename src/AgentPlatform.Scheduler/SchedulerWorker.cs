using AgentPlatform.Scheduler.Scheduling;

namespace AgentPlatform.Scheduler;

/// <summary>
/// Планировщик запусков агентов без входящего сообщения (например, агент качества).
/// Раз в <c>Scheduler:PollIntervalSeconds</c> секунд делает тик: выбирает созревшие
/// записи scheduled_runs и запускает сессии агентов через <see cref="ScheduledRunRunner"/>.
/// </summary>
public sealed class SchedulerWorker(
    ScheduledRunRunner runner,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<SchedulerWorker> logger) : BackgroundService
{
    private readonly ScheduledRunRunner _runner =
        runner ?? throw new ArgumentNullException(nameof(runner));

    private readonly IConfiguration _configuration =
        configuration ?? throw new ArgumentNullException(nameof(configuration));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<SchedulerWorker> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = ReadPollInterval();
        var interval = TimeSpan.FromSeconds(intervalSeconds);

        _logger.LogInformation(
            "Scheduler started: poll interval {IntervalSeconds}s, cron time zone UTC",
            intervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var launched = await _runner.RunOnceAsync(stoppingToken).ConfigureAwait(false);

                if (launched > 0)
                {
                    _logger.LogInformation(
                        "Scheduler tick at {Utc}: {Launched} scheduled runs launched",
                        _timeProvider.GetUtcNow(),
                        launched);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Ошибка одного тика не должна останавливать процесс.
                _logger.LogError(exception, "Scheduler tick failed");
            }

            try
            {
                await Task.Delay(interval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Scheduler stopped");
    }

    private int ReadPollInterval()
    {
        var rawInterval = _configuration["Scheduler:PollIntervalSeconds"];
        return int.TryParse(rawInterval, out var parsed) ? Math.Clamp(parsed, 5, 3600) : 30;
    }
}
