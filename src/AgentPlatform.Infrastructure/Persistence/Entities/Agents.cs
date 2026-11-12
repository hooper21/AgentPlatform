using AgentPlatform.Core.Abstractions;

namespace AgentPlatform.Infrastructure.Persistence.Entities;

/// <summary>Агент тенанта (таблица agents). current_version — указатель на опубликованную версию.</summary>
public sealed class Agent : ITenantEntity, IHasCreatedAt, IHasUpdatedAt
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int CurrentVersion { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<AgentVersion> Versions { get; set; } = new List<AgentVersion>();
}

/// <summary>
/// Неизменяемая версия агента (таблица agent_versions). Откат = переключение
/// agents.current_version, а не правка версии (PF2).
/// </summary>
public sealed class AgentVersion : IHasCreatedAt
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AgentId { get; set; }

    public int Version { get; set; }

    /// <summary>draft | proposed | published | archived.</summary>
    public string Status { get; set; } = "draft";

    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>jsonb: {endpoint?, model, routing}.</summary>
    public string ModelRef { get; set; } = "{}";

    /// <summary>jsonb: [{installation_id, tool_name, policy}].</summary>
    public string EnabledTools { get; set; } = "[]";

    /// <summary>jsonb: правила квалификации/ограничения (валидируется кодом, REQUIRED).</summary>
    public string Rules { get; set; } = "{}";

    /// <summary>jsonb: триггеры эскалации (sentiment, failed_turns, ...).</summary>
    public string EscalationRules { get; set; } = "{}";

    /// <summary>jsonb: лимиты max_turns, max_tool_calls, max_tokens.</summary>
    public string Limits { get; set; } = "{}";

    /// <summary>Черновик предложен агентом качества (platform-ops).</summary>
    public bool ProposedByOps { get; set; }

    /// <summary>null = создано системой/Scheduler.</summary>
    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Agent? Agent { get; set; }

    public User? Author { get; set; }
}
