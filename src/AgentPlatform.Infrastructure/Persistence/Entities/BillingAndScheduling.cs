using AgentPlatform.Core.Abstractions;

namespace AgentPlatform.Infrastructure.Persistence.Entities;

/// <summary>
/// Суточный учёт использования (таблица usage). В DDL нет created_at/updated_at:
/// период агрегации задаёт естественный ключ (tenant_id, period, agent_id).
/// </summary>
public sealed class UsageRecord : ITenantEntity
{
    public long Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>День агрегации (date).</summary>
    public DateOnly Period { get; set; }

    public Guid? AgentId { get; set; }

    public int Dialogs { get; set; }

    public int ToolCalls { get; set; }

    public long TokensIn { get; set; }

    public long TokensOut { get; set; }

    public Agent? Agent { get; set; }
}

/// <summary>Расписание запуска агента без входящего сообщения (таблица scheduled_runs).</summary>
public sealed class ScheduledRun : ITenantEntity, IHasCreatedAt, IHasUpdatedAt
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public Guid AgentId { get; set; }

    public string CronExpression { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>ok | error.</summary>
    public string? LastRunStatus { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Agent? Agent { get; set; }
}
