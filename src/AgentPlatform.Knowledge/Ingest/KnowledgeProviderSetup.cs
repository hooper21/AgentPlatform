using AgentPlatform.Core.Dto;

namespace AgentPlatform.Knowledge.Ingest;

/// <summary>Пространство/база знаний, предлагаемое мастеру подключения.</summary>
/// <param name="Key">Машинный ключ (spaceKey / id пространства).</param>
/// <param name="Name">Название для UI.</param>
/// <param name="Type">Тип пространства (global/personal) или null.</param>
/// <param name="Url">Ссылка на пространство (если известна).</param>
public sealed record KnowledgeSourceSpace(
    string Key,
    string Name,
    string? Type = null,
    string? Url = null);

/// <summary>
/// Дополнительный (не входящий в Core) контракт мастера подключения: список
/// пространств для чекбоксов и обязательное предупреждение об индексации.
/// <para>
/// TODO: contract — перенести в Core при расширении IKnowledgeProvider
/// (UI-мастер и предупреждение — часть платформенного контракта плагинов знания).
/// </para>
/// </summary>
public interface IKnowledgeProviderSetup
{
    /// <summary>Токен провайдера (knowledge_sources.provider_type).</summary>
    string ProviderType { get; }

    /// <summary>
    /// Предупреждение, которое кабинет показывает на шаге подключения.
    /// Для внешних систем: права доступа не переносятся в чат.
    /// </summary>
    string Warning { get; }

    /// <summary>Пространства, доступные подключённому токену.</summary>
    Task<IReadOnlyList<KnowledgeSourceSpace>> ListSpacesAsync(
        KnowledgeProviderConfig config,
        CancellationToken cancellationToken = default);
}
