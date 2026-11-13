using System.Text.Json;
using AgentPlatform.Core.Json;

namespace AgentPlatform.Knowledge.Contracts;

/// <summary>Создание источника знаний: тип — из реестра провайдеров.</summary>
/// <param name="Type">Тип провайдера: document | site | faq | тип плагина.</param>
/// <param name="Ref">Путь к файлу (document) или URL (site). Для faq — null.</param>
/// <param name="Title">Заголовок источника (попадает в блок RAG).</param>
/// <param name="Content">Контент ручной FAQ-записи (обязателен для type=faq).</param>
/// <param name="ReindexCron">Устаревшее имя расписания; используется, если не задан SyncSchedule.</param>
/// <param name="SyncSchedule">Cron-расписание синхронизации (UTC, 5 полей).</param>
/// <param name="CredentialsRef">Ссылка на секрет для доступа к источнику.</param>
/// <param name="Config">Дополнительные параметры провайдера (см. GetSourceInfo).</param>
public sealed record CreateKnowledgeSourceRequest(
    string? Type,
    string? Ref,
    string? Title,
    string? Content = null,
    string? ReindexCron = null,
    string? SyncSchedule = null,
    string? CredentialsRef = null,
    IReadOnlyDictionary<string, string?>? Config = null);

/// <summary>Представление источника знаний в API.</summary>
/// <param name="Id">Идентификатор источника.</param>
/// <param name="Type">Legacy-тип: document | site | faq.</param>
/// <param name="Ref">Путь/URL; null для faq.</param>
/// <param name="Title">Заголовок источника.</param>
/// <param name="ChunkCount">Число чанков после последней успешной индексации.</param>
/// <param name="IndexStatus">pending | indexing | ready | error.</param>
/// <param name="IndexMeta">jsonb index_meta (время прогона, ошибки, расписание).</param>
/// <param name="CreatedAt">Время создания.</param>
/// <param name="UpdatedAt">Время последнего изменения.</param>
/// <param name="ProviderType">Тип провайдера (document | site | faq | плагин | legacy).</param>
/// <param name="SyncSchedule">Cron-расписание синхронизации.</param>
/// <param name="LastCursor">Последний применённый курсор инкремента.</param>
/// <param name="Config">Конфигурация подключения (jsonb).</param>
public sealed record KnowledgeSourceResponse(
    Guid Id,
    string Type,
    string? Ref,
    string Title,
    int ChunkCount,
    string IndexStatus,
    JsonElement IndexMeta,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ProviderType = null,
    string? SyncSchedule = null,
    string? LastCursor = null,
    JsonElement? Config = null);

/// <summary>Проверка связи с источником без создания записи (TestConnection).</summary>
/// <param name="Title">Заголовок (для провайдеров, которым он нужен).</param>
/// <param name="Location">Путь к файлу / URL.</param>
/// <param name="Content">Ручной контент (faq).</param>
/// <param name="CredentialsRef">Ссылка на секрет.</param>
/// <param name="Config">Дополнительные параметры провайдера.</param>
public sealed record TestKnowledgeProviderRequest(
    string? Title,
    string? Location,
    string? Content,
    string? CredentialsRef = null,
    IReadOnlyDictionary<string, string?>? Config = null);

/// <summary>Внутреннее содержимое knowledge_sources.index_meta.</summary>
public sealed class KnowledgeIndexMeta
{
    /// <summary>Курсор провайдера после последнего инжеста (site — URL страницы).</summary>
    public string? Cursor { get; set; }

    /// <summary>Ручной контент FAQ (нужен для переиндексации).</summary>
    public string? ManualContent { get; set; }

    /// <summary>Cron-расписание переиндексации (UTC).</summary>
    public string? ReindexCron { get; set; }

    /// <summary>Время последней успешной индексации.</summary>
    public DateTimeOffset? LastIndexedAt { get; set; }

    /// <summary>Время последней попытки (успешной или нет).</summary>
    public DateTimeOffset? AttemptedAt { get; set; }

    /// <summary>Длительность последней попытки, мс.</summary>
    public long? DurationMs { get; set; }

    /// <summary>Число чанков последней успешной индексации.</summary>
    public int? Chunks { get; set; }

    /// <summary>Размерность эмбеддинга (должна совпадать с vector(768)).</summary>
    public int? Dimensions { get; set; }

    /// <summary>Текст ошибки последней попытки (источник не удаляется).</summary>
    public string? Error { get; set; }

    /// <summary>Тип ошибки последней попытки.</summary>
    public string? ErrorType { get; set; }

    /// <summary>Читает index_meta; пустой/битый документ — пустые метаданные.</summary>
    public static KnowledgeIndexMeta Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new KnowledgeIndexMeta();
        }

        try
        {
            return PlatformJson.DeserializeDb<KnowledgeIndexMeta>(json) ?? new KnowledgeIndexMeta();
        }
        catch (JsonException)
        {
            return new KnowledgeIndexMeta();
        }
    }

    /// <summary>Сериализует метаданные для jsonb-столбца.</summary>
    public string ToJson() => PlatformJson.SerializeDb(this);
}

/// <summary>jsonb-метаданные чанка: источник, заголовок, позиция, число токенов.</summary>
/// <param name="Source">Заголовок источника.</param>
/// <param name="SourceType">Тип источника.</param>
/// <param name="Heading">Заголовок раздела.</param>
/// <param name="Position">Позиция чанка в источнике.</param>
/// <param name="Tokens">Число токенов чанка.</param>
/// <param name="Cursor">Курсор страницы (site — время обхода, document — время файла).</param>
/// <param name="ExternalId">Идентификатор страницы источника (для удаления tombstone-страниц).</param>
/// <param name="Url">URL/путь страницы.</param>
/// <param name="ModifiedAt">Дата изменения страницы в источнике.</param>
/// <param name="Author">Автор/владелец страницы.</param>
/// <param name="Space">Пространство/база знаний.</param>
/// <param name="Folder">Папка/раздел.</param>
public sealed record KnowledgeChunkMetadata(
    string Source,
    string SourceType,
    string? Heading,
    int Position,
    int Tokens,
    string? Cursor = null,
    string? ExternalId = null,
    string? Url = null,
    DateTimeOffset? ModifiedAt = null,
    string? Author = null,
    string? Space = null,
    string? Folder = null)
{
    /// <summary>Сериализует метаданные для jsonb-столбца knowledge_chunks.metadata.</summary>
    public string ToJson() => PlatformJson.SerializeDb(this);
}
