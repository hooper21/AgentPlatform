namespace AgentPlatform.Core.Dto;

/// <summary>
/// Токены встроенных провайдеров знаний (значение knowledge_sources.provider_type).
/// Строка, а не enum: новый провайдер — плагин, а не правка Core.
/// </summary>
public static class KnowledgeProviderTypes
{
    /// <summary>
    /// Значение provider_type у источников, созданных до появления контракта.
    /// Для них провайдер определяется по legacy-колонке knowledge_sources.type.
    /// </summary>
    public const string Legacy = "legacy";

    /// <summary>Файл MD/TXT.</summary>
    public const string Document = "document";

    /// <summary>Обход сайта по HTTP.</summary>
    public const string Site = "site";

    /// <summary>Ручная FAQ-запись.</summary>
    public const string Faq = "faq";
}

/// <summary>Универсальные имена параметров подключения, которые понимает общий пайплайн.</summary>
public static class KnowledgeProviderParameterNames
{
    /// <summary>Путь к файлу или URL источника (для faq — отсутствует).</summary>
    public const string Location = "location";

    /// <summary>Контент, переданный вручную (для faq).</summary>
    public const string Content = "content";
}

/// <summary>Типы подключения провайдера (справочная информация для UI).</summary>
public static class KnowledgeConnectionTypes
{
    /// <summary>Локальный/сетевой файл.</summary>
    public const string File = "file";

    /// <summary>HTTP(S)-источник.</summary>
    public const string Http = "http";

    /// <summary>Данные вводятся вручную.</summary>
    public const string Manual = "manual";
}

/// <summary>
/// Описание провайдера знаний: что за источник, как подключается и какие
/// параметры настраиваются. Используется кабинетом для выбора типа источника.
/// </summary>
/// <param name="ProviderType">Токен типа (knowledge_sources.provider_type).</param>
/// <param name="DisplayName">Человекочитаемое название для UI.</param>
/// <param name="ConnectionType">file | http | manual | тип плагина.</param>
/// <param name="Description">Краткое описание назначения провайдера.</param>
/// <param name="Parameters">Настраиваемые параметры подключения.</param>
/// <param name="SupportsIncremental">Поддерживает ли FetchIncremental.</param>
/// <param name="SupportsWebhooks">Может ли источник инициировать синхронизацию вебхуком.</param>
public sealed record KnowledgeProviderInfo(
    string ProviderType,
    string DisplayName,
    string ConnectionType,
    string Description,
    IReadOnlyList<KnowledgeProviderParameter> Parameters,
    bool SupportsIncremental = false,
    bool SupportsWebhooks = false);

/// <summary>Настраиваемый параметр подключения провайдера.</summary>
/// <param name="Name">Машинное имя (ключ config).</param>
/// <param name="DisplayName">Название для UI.</param>
/// <param name="Required">Обязателен ли параметр при создании источника.</param>
/// <param name="Description">Подсказка для оператора.</param>
/// <param name="DefaultValue">Значение по умолчанию (если есть).</param>
public sealed record KnowledgeProviderParameter(
    string Name,
    string DisplayName,
    bool Required = false,
    string? Description = null,
    string? DefaultValue = null);


/// <summary>
/// Конфигурация подключения источника (knowledge_sources.config).
/// Провайдер получает её целиком и не знает, откуда взялись значения.
/// </summary>
/// <param name="Title">Заголовок источника (для метаданных страниц).</param>
/// <param name="Location">Путь к файлу / URL (для faq — null).</param>
/// <param name="Content">Контент, переданный вручную (для faq).</param>
/// <param name="CredentialsRef">Ссылка на секрет (credentials_ref), если нужен доступ.</param>
/// <param name="Parameters">Дополнительные параметры провайдера (см. GetSourceInfo).</param>
public sealed record KnowledgeProviderConfig(
    string Title,
    string? Location = null,
    string? Content = null,
    string? CredentialsRef = null,
    IReadOnlyDictionary<string, string?>? Parameters = null)
{
    /// <summary>Значение дополнительного параметра или null.</summary>
    public string? GetParameter(string name)
        => Parameters is not null && Parameters.TryGetValue(name, out var value) ? value : null;
}

/// <summary>Статус связи с источником (результат TestConnection).</summary>
/// <param name="IsHealthy">true — источник доступен и готов к чтению.</param>
/// <param name="Message">Короткое сообщение для UI/логов.</param>
/// <param name="CheckedAt">Время проверки (UTC).</param>
/// <param name="Error">Техническая деталь ошибки (если есть).</param>
public sealed record KnowledgeConnectionStatus(
    bool IsHealthy,
    string Message,
    DateTimeOffset CheckedAt,
    string? Error = null)
{
    /// <summary>Связь установлена.</summary>
    public static KnowledgeConnectionStatus Healthy(string message, DateTimeOffset checkedAt)
        => new(true, message, checkedAt);

    /// <summary>Связь не установлена (исключение не пробрасывается).</summary>
    public static KnowledgeConnectionStatus Unhealthy(
        string message,
        DateTimeOffset checkedAt,
        string? error = null)
        => new(false, message, checkedAt, error);
}

/// <summary>
/// Метаданные страницы источника. Провайдер отдаёт их как есть, пайплайн
/// сохраняет в jsonb-метаданные чанков.
/// </summary>
/// <param name="Author">Автор/владелец страницы.</param>
/// <param name="ModifiedAt">Дата изменения на стороне источника.</param>
/// <param name="Url">Канонический URL страницы.</param>
/// <param name="Space">Пространство/база знаний (например, хост сайта).</param>
/// <param name="Folder">Папка/раздел внутри пространства.</param>
/// <param name="Extra">Провайдер-специфичные метаданные.</param>
public sealed record KnowledgePageMetadata(
    string? Author = null,
    DateTimeOffset? ModifiedAt = null,
    string? Url = null,
    string? Space = null,
    string? Folder = null,
    IReadOnlyDictionary<string, string?>? Extra = null)
{
    /// <summary>Пустые метаданные.</summary>
    public static KnowledgePageMetadata Empty { get; } = new();
}

/// <summary>
/// Сырая страница источника, которую отдаёт провайдер. Провайдер не знает
/// про чанкинг, эмбеддинги и pgvector — это забота общего пайплайна.
/// </summary>
/// <param name="ExternalId">
/// Стабильный идентификатор страницы на стороне источника. По нему удаляются
/// чанки при isDeleted=true (metadata.external_id в knowledge_chunks).
/// </param>
/// <param name="Title">Заголовок страницы.</param>
/// <param name="Content">Текст страницы без разметки.</param>
/// <param name="Metadata">Автор, дата изменения, URL, пространство/папка.</param>
/// <param name="IsDeleted">true — страница удалена в источнике (tombstone).</param>
/// <param name="Heading">Заголовок раздела внутри страницы (если есть).</param>
/// <param name="Position">Позиция страницы/раздела в источнике (0-based).</param>
/// <param name="Cursor">
/// Курсор состояния после этой страницы (для полной синхронизации). Пайплайн
/// сохраняет последний непустой курсор в knowledge_sources.last_cursor.
/// </param>
public sealed record KnowledgePage(
    string ExternalId,
    string Title,
    string Content,
    KnowledgePageMetadata? Metadata = null,
    bool IsDeleted = false,
    string? Heading = null,
    int Position = 0,
    string? Cursor = null)
{
    /// <summary>Метаданные страницы (никогда не null).</summary>
    public KnowledgePageMetadata PageMetadata => Metadata ?? KnowledgePageMetadata.Empty;
}

/// <summary>
/// Изменение, полученное инкрементальным чтением, и курсор после его применения.
/// Пустой поток означает «изменений нет»: курсор остаётся прежним.
/// </summary>
/// <param name="Page">Страница (для tombstone — с IsDeleted=true).</param>
/// <param name="Cursor">Новый курсор после применения изменения.</param>
public sealed record KnowledgeChange(KnowledgePage Page, string? Cursor);
