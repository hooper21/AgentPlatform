namespace AgentPlatform.Scheduler.Scheduling;

/// <summary>
/// Системный триггер планового запуска: Scheduler стартует ход агента без сообщения
/// визитера. Текст триггера уходит в Runtime как входящее сообщение, а его
/// происхождение фиксируется в metadata (incoming.metadata.trigger = schedule).
/// </summary>
public static class ScheduledTrigger
{
    /// <summary>Текст системного триггера.</summary>
    public const string Text = "плановый запуск";

    /// <summary>Ключ metadata с причиной запуска.</summary>
    public const string MetadataKey = "trigger";

    /// <summary>Значение metadata для cron-запуска.</summary>
    public const string MetadataValue = "schedule";

    /// <summary>Ключ metadata с идентификатором строки scheduled_runs.</summary>
    public const string ScheduledRunIdMetadataKey = "scheduled_run_id";
}
