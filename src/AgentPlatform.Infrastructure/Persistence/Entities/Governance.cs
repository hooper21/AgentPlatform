using AgentPlatform.Core.Abstractions;

namespace AgentPlatform.Infrastructure.Persistence.Entities;

/// <summary>
/// Заявка HITL-гейта (таблица approvals). gate_payload валидируется кодом (REQUIRED).
/// </summary>
public sealed class Approval : ITenantEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SessionId { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>Чекпоинт, из которого возобновлять сессию.</summary>
    public long? CheckpointId { get; set; }

    /// <summary>jsonb: {tool, args_masked, human_description, undo_hint}.</summary>
    public string GatePayload { get; set; } = "{}";

    /// <summary>pending | approved | rejected | expired | superseded.</summary>
    public string Status { get; set; } = "pending";

    public DateTimeOffset RequestedAt { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>null, если решение принял пользователь канала.</summary>
    public Guid? DecidedBy { get; set; }

    /// <summary>operator | user | system.</summary>
    public string? DecidedByKind { get; set; }

    public string? Decision { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public AgentSession? Session { get; set; }

    public Checkpoint? Checkpoint { get; set; }

    public User? DecisionAuthor { get; set; }
}

/// <summary>
/// Аудит вызовов тулов (таблица tool_audit). Append-only:
/// UPDATE/DELETE запрещены кодом (см. AgentPlatformDbContext.ValidateBeforeSave).
/// </summary>
public sealed class ToolAuditEntry : ITenantEntity
{
    public long Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid SessionId { get; set; }

    public string ToolName { get; set; } = string.Empty;

    public Guid InstallationId { get; set; }

    /// <summary>jsonb: аргументы с замаскированными ПДн.</summary>
    public string ArgsMasked { get; set; } = "{}";

    /// <summary>ok | error | denied | degraded | gate_required.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Ссылка на хранилище полного результата.</summary>
    public string? ResultRef { get; set; }

    public int? LatencyMs { get; set; }

    /// <summary>OTel trace correlation.</summary>
    public string TraceId { get; set; } = string.Empty;

    public DateTimeOffset Ts { get; set; }

    public AgentSession? Session { get; set; }

    public ConnectorInstallation? Installation { get; set; }
}
