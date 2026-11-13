using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;

namespace AgentPlatform.Core.Abstractions;

/// <summary>
/// Контракт приёмника контента для инжеста знаний: отдаёт поток страниц/документов
/// с метаданными и инкрементальными курсорами. Встроенные реализации —
/// document | site | faq. Внешние системы знаний подключаются плагинами поверх
/// этого же контракта (Knowledge Service не зависит от коннекторов и MCP).
/// </summary>
public interface IKnowledgeProvider
{
    /// <summary>Тип источника, который обслуживает провайдер.</summary>
    KnowledgeSourceType SourceType { get; }

    /// <summary>
    /// Читает страницы источника. Курсор (<see cref="KnowledgeProviderRequest.Cursor"/>)
    /// позволяет продолжить инкрементальное чтение с места, сохранённого в index_meta.
    /// </summary>
    IAsyncEnumerable<KnowledgePage> ReadAsync(
        KnowledgeProviderRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Параметры чтения источника знаний.</summary>
/// <param name="Title">Заголовок источника.</param>
/// <param name="Ref">Путь к файлу / URL / прочее (для faq — null).</param>
/// <param name="Content">Контент, переданный вручную (для faq).</param>
/// <param name="Cursor">Курсор инкрементального чтения из index_meta (если есть).</param>
public sealed record KnowledgeProviderRequest(
    string Title,
    string? Ref,
    string? Content = null,
    string? Cursor = null);
