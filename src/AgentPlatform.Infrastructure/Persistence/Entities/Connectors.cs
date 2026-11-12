using AgentPlatform.Core.Abstractions;

namespace AgentPlatform.Infrastructure.Persistence.Entities;

/// <summary>
/// Установка коннектора в тенанте (таблица connector_installations).
/// Секрет хранится только по ссылке (credentials_ref), не в БД платформы.
/// </summary>
public sealed class ConnectorInstallation : ITenantEntity, IHasCreatedAt, IHasUpdatedAt
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public Guid RegistryId { get; set; }

    /// <summary>Ссылка на секрет в Secret Service.</summary>
    public string CredentialsRef { get; set; } = string.Empty;

    /// <summary>apiKey | longLivedToken | oauth2 | external_mcp.</summary>
    public string AuthType { get; set; } = string.Empty;

    /// <summary>URL стороннего MCP-сервера (для external_mcp).</summary>
    public string? ExternalUrl { get; set; }

    /// <summary>active | error | disabled.</summary>
    public string Status { get; set; } = "active";

    public DateTimeOffset? LastHealthCheck { get; set; }

    /// <summary>jsonb: счётчики ошибок, 429, последняя ошибка.</summary>
    public string HealthMeta { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ConnectorRegistryEntry? Registry { get; set; }

    public ICollection<ToolSetting> ToolSettings { get; set; } = new List<ToolSetting>();
}

/// <summary>
/// Настройка тула (таблица tool_settings). В DDL нет tenant_id:
/// изоляция выполняется через connector_installations (см. TODO в конфигурации).
/// </summary>
public sealed class ToolSetting : IHasCreatedAt, IHasUpdatedAt
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid InstallationId { get; set; }

    public string ToolName { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>read | write | destructive (понижение допустимо, повышение — нет).</summary>
    public string Policy { get; set; } = string.Empty;

    /// <summary>manifest | tenant_override.</summary>
    public string PolicySource { get; set; } = "manifest";

    public string? DescriptionOverride { get; set; }

    /// <summary>jsonb: {mode: always|conditional|never, predicate?}.</summary>
    public string GateSettings { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ConnectorInstallation? Installation { get; set; }
}
