using AgentPlatform.Core.Abstractions;

namespace AgentPlatform.Infrastructure.Persistence.Entities;

/// <summary>
/// Сессия диалога (таблица sessions). agent_version фиксируется на старте (version pinning).
/// </summary>
public sealed class AgentSession : ITenantEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public Guid AgentId { get; set; }

    public int AgentVersion { get; set; }

    public Guid ChannelId { get; set; }

    /// <summary>Идентификатор визитера канала.</summary>
    public string UserRef { get; set; } = string.Empty;

    /// <summary>active | awaiting_gate | escalated | operator | closed | degraded.</summary>
    public string Status { get; set; } = "active";

    /// <summary>jsonb: счётчики ходов, флаги эскалации, debounce.</summary>
    public string Meta { get; set; } = "{}";

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset LastActivityAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public Agent? Agent { get; set; }

    public Channel? Channel { get; set; }

    public ICollection<Message> Messages { get; set; } = new List<Message>();

    public ICollection<Checkpoint> Checkpoints { get; set; } = new List<Checkpoint>();
}

/// <summary>Сообщение диалога (таблица messages). Удаляется каскадно вместе с сессией.</summary>
public sealed class Message : ITenantEntity
{
    public long Id { get; set; }

    public Guid SessionId { get; set; }

    /// <summary>Денормализация tenant_id для tenant-фильтров.</summary>
    public Guid TenantId { get; set; }

    /// <summary>user | assistant | tool | operator | system.</summary>
    public string Role { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    /// <summary>jsonb: [{name, args_masked, result_ref, latency_ms}].</summary>
    public string? ToolCalls { get; set; }

    public DateTimeOffset Ts { get; set; }

    public AgentSession? Session { get; set; }
}

/// <summary>
/// Чекпоинт сессии (таблица checkpoints). state — нейтральный контракт платформы,
/// а не формат рантайма. Удаляется каскадно вместе с сессией.
/// </summary>
public sealed class Checkpoint : ITenantEntity, IHasCreatedAt
{
    public long Id { get; set; }

    public Guid SessionId { get; set; }

    public Guid TenantId { get; set; }

    public int Superstep { get; set; }

    /// <summary>jsonb: {messages, pending_gate, counters, version}.</summary>
    public string State { get; set; } = "{}";

    /// <summary>sha256 топологии графа для валидации resume.</summary>
    public string GraphSignatureHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public AgentSession? Session { get; set; }
}
