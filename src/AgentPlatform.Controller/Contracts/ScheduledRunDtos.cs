namespace AgentPlatform.Controller.Contracts;

/// <summary>
/// Создание расписания планировщика. Tenant_id берётся из маршрута:
/// Controller API — внутренний API платформы, тело запроса тенант не задаёт.
/// </summary>
/// <param name="AgentId">Агент, который запускается по cron.</param>
/// <param name="CronExpression">Cron из 5 полей (время UTC).</param>
/// <param name="Enabled">false — расписание создано, но не выполняется.</param>
public sealed record ScheduledRunCreateRequest(
    Guid AgentId,
    string CronExpression,
    bool Enabled = true);

/// <summary>Изменение расписания планировщика.</summary>
/// <param name="AgentId">Агент, который запускается по cron.</param>
/// <param name="CronExpression">Cron из 5 полей (время UTC).</param>
/// <param name="Enabled">false — расписание приостановлено.</param>
public sealed record ScheduledRunUpdateRequest(
    Guid AgentId,
    string CronExpression,
    bool Enabled);

/// <summary>Расписание в ответах Controller API.</summary>
public sealed record ScheduledRunResponse(
    Guid Id,
    Guid TenantId,
    Guid AgentId,
    string CronExpression,
    bool Enabled,
    DateTimeOffset? LastRunAt,
    string? LastRunStatus,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
