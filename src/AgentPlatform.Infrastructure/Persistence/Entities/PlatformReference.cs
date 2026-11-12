using AgentPlatform.Core.Abstractions;

namespace AgentPlatform.Infrastructure.Persistence.Entities;

/// <summary>
/// Тенант (таблица tenants). Справочник платформы, tenant-фильтр не применяется.
/// </summary>
public sealed class Tenant : IHasCreatedAt, IHasUpdatedAt
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>pilot | basic | unlimited.</summary>
    public string Plan { get; set; } = "pilot";

    /// <summary>jsonb: {mode, endpoint?, api_key_ref?, model}.</summary>
    public string LlmConfig { get; set; } = "{}";

    /// <summary>active | blocked.</summary>
    public string Status { get; set; } = "active";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Пользователь кабинета тенанта (таблица users). Email уникален внутри тенанта.
/// </summary>
public sealed class User : ITenantEntity, IHasCreatedAt, IHasUpdatedAt
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public string Email { get; set; } = string.Empty;

    /// <summary>PBKDF2-хеш пароля (см. Infrastructure.Seeding.Pbkdf2PasswordHasher).</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>admin | operator | viewer.</summary>
    public string Role { get; set; } = "viewer";

    /// <summary>jsonb: метаданные аутентификации (MFA, блокировки, last_login).</summary>
    public string AuthMeta { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Каталог коннекторов платформы (таблица connector_registry). Глобальная таблица.
/// </summary>
public sealed class ConnectorRegistryEntry : IHasCreatedAt, IHasUpdatedAt
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    /// <summary>jsonb: ConnectorManifest + JSON Schema тулов.</summary>
    public string Manifest { get; set; } = "{}";

    /// <summary>sha256 манифеста для верификации.</summary>
    public string ManifestHash { get; set; } = string.Empty;

    public string Publisher { get; set; } = string.Empty;

    /// <summary>draft | published | deprecated.</summary>
    public string Status { get; set; } = "published";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Шаблон решения (таблица solution_profiles). Глобальная таблица платформы.
/// </summary>
public sealed class SolutionProfile : IHasCreatedAt, IHasUpdatedAt
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    /// <summary>jsonb: {agent, connectors, knowledgeSources, channels, escalationRules}.</summary>
    public string Config { get; set; } = "{}";

    public string Publisher { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
