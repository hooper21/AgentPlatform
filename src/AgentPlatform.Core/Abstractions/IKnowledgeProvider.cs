using AgentPlatform.Core.Dto;

namespace AgentPlatform.Core.Abstractions;

/// <summary>
/// Контракт провайдера знаний: приёмник сырого контента для универсального
/// инжест-пайплайна. Аналогично <see cref="IChannelAdapter"/> провайдер — плагин:
/// новый тип источника не требует правки Core и пайплайна.
/// <para>
/// Провайдер отвечает только за подключение к источнику и выдачу страниц с
/// метаданными и курсорами. Он НЕ знает про чанкинг, эмбеддинги, pgvector и
/// tenant-изоляцию — всё это делает общий пайплайн Knowledge Service.
/// </para>
/// </summary>
public interface IKnowledgeProvider
{
    /// <summary>
    /// Описание провайдера: название, тип подключения и настраиваемые параметры.
    /// Именно этот список показывает кабинет при выборе типа источника.
    /// </summary>
    KnowledgeProviderInfo GetSourceInfo();

    /// <summary>
    /// Проверяет связь с источником по конфигурации подключения. Ошибка доступа
    /// возвращается как <see cref="KnowledgeConnectionStatus.IsHealthy"/> = false,
    /// а не исключением: UI показывает статус, источник не создаётся.
    /// </summary>
    Task<KnowledgeConnectionStatus> TestConnection(
        KnowledgeProviderConfig config,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Полное чтение источника. <paramref name="since"/> = null — весь контент;
    /// значение позволяет провайдеру отфильтровать заведомо неизменившиеся
    /// страницы (например, по Last-Modified). Возвращает поток страниц.
    /// </summary>
    IAsyncEnumerable<KnowledgePage> FetchAll(
        KnowledgeProviderConfig config,
        DateTimeOffset? since = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Инкрементальное чтение: изменения после сохранённого курсора. Для каждой
    /// страницы провайдер возвращает новый курсор; пайплайн сохраняет последний.
    /// Удалённые в источнике страницы отдаются с <see cref="KnowledgePage.IsDeleted"/>.
    /// </summary>
    IAsyncEnumerable<KnowledgeChange> FetchIncremental(
        KnowledgeProviderConfig config,
        string? cursor = null,
        CancellationToken cancellationToken = default);
}
