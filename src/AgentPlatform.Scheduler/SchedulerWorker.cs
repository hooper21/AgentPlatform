namespace AgentPlatform.Scheduler;

/// <summary>
/// Планировщик запусков агентов без входящего сообщения (например, агент качества).
/// В v1 — заглушка: читает интервал опроса и пишет в лог; cron-исполнение
/// и работа с scheduled_runs появятся в задаче 2.4.
/// </summary>
public sealed class SchedulerWorker(ILogger<SchedulerWorker> logger, IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var rawInterval = configuration["Scheduler:PollIntervalSeconds"];
        var intervalSeconds = int.TryParse(rawInterval, out var parsed) ? Math.Clamp(parsed, 5, 3600) : 60;
        var interval = TimeSpan.FromSeconds(intervalSeconds);

        logger.LogInformation("Scheduler stub started, poll interval {IntervalSeconds}s", intervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            // TODO(2.4): выбрать scheduled_runs (enabled = true), проверить cron,
            // запустить сессию через IOrchestrationRuntime и записать last_run_at/status.
            logger.LogInformation("Scheduler stub tick at {Utc}: no scheduled runs executed yet", DateTimeOffset.UtcNow);

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Scheduler stub stopped");
    }
}
