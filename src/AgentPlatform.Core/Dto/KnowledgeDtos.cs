namespace AgentPlatform.Core.Dto;

/// <summary>Запрос поиска по базе знаний (RAG).</summary>
/// <param name="TenantId">Тенант, по данным которого ищем.</param>
/// <param name="Query">Текст запроса.</param>
/// <param name="Top">Сколько чанков вернуть.</param>
public sealed record KnowledgeSearchRequest(Guid TenantId, string Query, int Top = 5);

/// <summary>Найденный чанк знаний.</summary>
/// <param name="SourceId">Источник знаний.</param>
/// <param name="ChunkId">Идентификатор чанка.</param>
/// <param name="Content">Текст чанка.</param>
/// <param name="Score">Релевантность (косинусное сходство).</param>
public sealed record KnowledgeHit(Guid SourceId, long ChunkId, string Content, double Score);
