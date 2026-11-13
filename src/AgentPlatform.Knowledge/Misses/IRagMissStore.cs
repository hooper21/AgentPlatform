namespace AgentPlatform.Knowledge.Misses;

/// <summary>
/// Журнал вопросов без ответа (таблица rag_misses): запросы, по которым RAG
/// не нашёл ни одного чанка выше порога. Источник метрики «вопросы без ответа»
/// (задача 3.3, коннектор platform-ops).
/// </summary>
public interface IRagMissStore
{
    /// <summary>Записывает промах поиска. Ошибка логирования не должна ломать диалог.</summary>
    Task LogAsync(
        Guid tenantId,
        string query,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default);
}
