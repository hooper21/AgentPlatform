using AgentPlatform.Core.Abstractions;

namespace AgentPlatform.Infrastructure.Persistence.Entities;

/// <summary>Канал тенанта (таблица channels): widget | telegram | плагины.</summary>
public sealed class Channel : ITenantEntity, IHasCreatedAt, IHasUpdatedAt
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    /// <summary>widget | telegram | ...</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>jsonb: брендирование, настройки канала.</summary>
    public string Config { get; set; } = "{}";

    /// <summary>Ссылка на секрет подписи JWT виджета.</summary>
    public string? WidgetSecretRef { get; set; }

    /// <summary>active | disabled.</summary>
    public string Status { get; set; } = "active";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Источник знаний (таблица knowledge_sources).</summary>
public sealed class KnowledgeSource : ITenantEntity, IHasCreatedAt, IHasUpdatedAt
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    /// <summary>
    /// Legacy-тип источника: document | site | faq. Оставлен для совместимости
    /// поиска/метаданных; маршрутизация идёт по <see cref="ProviderType"/>.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Тип провайдера (document | site | faq | тип плагина). У источников,
    /// созданных до контракта, — legacy: провайдер определяется по Type.
    /// </summary>
    public string ProviderType { get; set; } = "legacy";

    /// <summary>Путь к файлу / URL / прочее (legacy-представление config.location).</summary>
    public string Ref { get; set; } = string.Empty;

    /// <summary>jsonb: конфигурация подключения (location, content, параметры).</summary>
    public string Config { get; set; } = "{}";

    /// <summary>Ссылка на секрет (credentials_ref) для доступа к источнику.</summary>
    public string? CredentialsRef { get; set; }

    public string Title { get; set; } = string.Empty;

    public int ChunkCount { get; set; }

    /// <summary>pending | indexing | ready | error.</summary>
    public string IndexStatus { get; set; } = "pending";

    /// <summary>Курсор инкрементальной синхронизации (последний применённый).</summary>
    public string? LastCursor { get; set; }

    /// <summary>Cron-расписание синхронизации (UTC, 5 полей).</summary>
    public string? SyncSchedule { get; set; }

    /// <summary>jsonb: ошибки инжеста, время последнего прогона.</summary>
    public string IndexMeta { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Чанк источника знаний (таблица knowledge_chunks). Столбец embedding vector(1024)
/// создаётся миграцией через pgvector и в EF-модель не маппится (см. README, решение).
/// </summary>
public sealed class KnowledgeChunk : ITenantEntity, IHasCreatedAt
{
    public long Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid SourceId { get; set; }

    public string Content { get; set; } = string.Empty;

    /// <summary>jsonb: заголовки, позиция, страница.</summary>
    public string Metadata { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public KnowledgeSource? Source { get; set; }
}
