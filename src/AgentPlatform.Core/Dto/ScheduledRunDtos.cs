namespace AgentPlatform.Core.Dto;

/// <summary>
/// Строка расписания планировщика (таблица scheduled_runs): cron-запуск агента
/// без входящего сообщения.
/// </summary>
/// <param name="Id">Идентификатор расписания.</param>
/// <param name="TenantId">Тенант, которому принадлежит агент.</param>
/// <param name="AgentId">Запускаемый агент.</param>
/// <param name="CronExpression">Cron-выражение (5 полей, время UTC).</param>
/// <param name="Enabled">false — расписание не выполняется.</param>
/// <param name="LastRunAt">Момент последнего запуска (UTC).</param>
/// <param name="LastRunStatus">ok | error — итог последнего запуска.</param>
/// <param name="CreatedAt">Создано (UTC).</param>
/// <param name="UpdatedAt">Изменено (UTC).</param>
public sealed record ScheduledRunInfo(
    Guid Id,
    Guid TenantId,
    Guid AgentId,
    string CronExpression,
    bool Enabled,
    DateTimeOffset? LastRunAt,
    string? LastRunStatus,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
