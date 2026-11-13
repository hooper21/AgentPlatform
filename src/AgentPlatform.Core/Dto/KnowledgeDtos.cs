namespace AgentPlatform.Core.Dto;

/// <summary>Запрос поиска по базе знаний (RAG).</summary>
/// <param name="TenantId">Тенант, по данным которого ищем.</param>
/// <param name="Query">Текст запроса.</param>
/// <param name="Top">Сколько чанков вернуть.</param>
/// <param name="Threshold">Минимальное косинусное сходство (0..1), ниже — не отдаётся.</param>
public sealed record KnowledgeSearchRequest(
    Guid TenantId,
    string Query,
    int Top = 5,
    double Threshold = KnowledgeContext.DefaultThreshold);

/// <summary>Найденный чанк знаний.</summary>
/// <param name="SourceId">Источник знаний.</param>
/// <param name="ChunkId">Идентификатор чанка.</param>
/// <param name="Content">Текст чанка.</param>
/// <param name="Score">Релевантность (косинусное сходство).</param>
/// <param name="Title">Заголовок источника (для блока RAG-контекста).</param>
/// <param name="SourceType">Тип источника: document | site | faq.</param>
public sealed record KnowledgeHit(
    Guid SourceId,
    long ChunkId,
    string Content,
    double Score,
    string? Title = null,
    string? SourceType = null);

/// <summary>
/// Результат RAG-поиска для одного хода диалога: запрос и найденные чанки.
/// Пустой результат — <c>Hits = []</c>: диалог продолжается без справочного блока.
/// </summary>
/// <param name="Query">Запрос, по которому искали.</param>
/// <param name="Hits">Найденные чанки (отсортированы по убыванию релевантности).</param>
public sealed record KnowledgeContext(string Query, IReadOnlyList<KnowledgeHit> Hits)
{
    /// <summary>Порог релевантности по умолчанию (требование платформы).</summary>
    public const double DefaultThreshold = 0.77;

    /// <summary>Сколько чанков подставляется в контекст по умолчанию.</summary>
    public const int DefaultTopK = 5;

    /// <summary>true — поиск не дал результатов.</summary>
    public bool IsEmpty => Hits.Count == 0;

    /// <summary>Пустой результат поиска (диалог не ломается).</summary>
    public static KnowledgeContext Empty(string query) => new(query, []);
}


