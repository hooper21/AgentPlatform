namespace AgentPlatform.Knowledge.Embeddings;

/// <summary>
/// Эмбеддер знаний: вектор для текста в разрезе тенанта (модель берётся из
/// llm_config тенанта через LLM Gateway).
/// </summary>
public interface IKnowledgeEmbedder
{
    /// <summary>Ожидаемая размерность вектора (knowledge_chunks.embedding).</summary>
    int Dimensions { get; }

    /// <summary>Считает эмбеддинг текста для тенанта.</summary>
    Task<ReadOnlyMemory<float>> EmbedAsync(
        Guid tenantId,
        string text,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Размерность вектора не совпала с knowledge_chunks.embedding.
/// Данные НЕ подгоняются (не обрезаются и не дополняются): индексирование
/// останавливается с понятной ошибкой в index_meta.
/// </summary>
public sealed class EmbeddingDimensionMismatchException : InvalidOperationException
{
    /// <summary>Создаёт исключение с фактической и ожидаемой размерностью.</summary>
    public EmbeddingDimensionMismatchException(string message, int actual, int expected)
        : base(message)
    {
        Actual = actual;
        Expected = expected;
    }

    /// <summary>Фактическая размерность вектора.</summary>
    public int Actual { get; }

    /// <summary>Ожидаемая размерность (knowledge_chunks.embedding).</summary>
    public int Expected { get; }
}
