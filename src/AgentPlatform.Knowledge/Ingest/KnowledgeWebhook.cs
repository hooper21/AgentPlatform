using System.Text.Json;

namespace AgentPlatform.Knowledge.Ingest;

/// <summary>Нормализованные типы событий внешних систем знаний.</summary>
public static class KnowledgeWebhookEventTypes
{
    /// <summary>Страница создана.</summary>
    public const string Created = "page_created";

    /// <summary>Страница изменена.</summary>
    public const string Updated = "page_updated";

    /// <summary>Страница удалена/перемещена в корзину.</summary>
    public const string Trashed = "page_trashed";
}

/// <summary>Разобранное событие вебхука внешней системы знаний.</summary>
/// <param name="EventType">Нормализованный тип (см. <see cref="KnowledgeWebhookEventTypes"/>).</param>
/// <param name="ExternalId">Идентификатор страницы в источнике (external_id чанков).</param>
/// <param name="SpaceKey">Ключ пространства, если передан.</param>
/// <param name="OccurredAt">Время события на стороне источника (UTC), если известно.</param>
/// <param name="IsDeletion">true — страницу нужно удалить из индекса.</param>
public sealed record KnowledgeWebhookEvent(
    string EventType,
    string? ExternalId,
    string? SpaceKey,
    DateTimeOffset? OccurredAt,
    bool IsDeletion);

/// <summary>
/// Разбор вебхука провайдера знаний. Формат payload специфичен для внешней
/// системы, поэтому парсер — часть плагина.
/// <para>
/// TODO: contract — регистрация вебхуков, проверка подписи (Connect JWT) и
/// повторная доставка будут описаны платформенным контрактом плагина.
/// </para>
/// </summary>
public interface IKnowledgeWebhookParser
{
    /// <summary>Умеет ли парсер обрабатывать события провайдера.</summary>
    bool CanParse(string? providerType);

    /// <summary>
    /// Разбирает payload; false — событие не относится к изменениям страниц.
    /// <paramref name="eventHint"/> — имя события из регистрации вебхука
    /// (Connect передаёт событие не в теле, а в URL/дескрипторе модуля).
    /// </summary>
    bool TryParse(JsonElement payload, out KnowledgeWebhookEvent @event, string? eventHint = null);
}
