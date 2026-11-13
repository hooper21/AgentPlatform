namespace AgentPlatform.Knowledge.Configuration;

/// <summary>
/// Настройки Knowledge Service: чанкинг, эмбеддер, поиск и переиндексация.
/// Секретов не содержит: доступ к LLM идёт через LLM Gateway по llm_config тенанта.
/// </summary>
public sealed class KnowledgeOptions
{
    /// <summary>Секция конфигурации (appsettings / env <c>Knowledge__*</c>).</summary>
    public const string SectionName = "Knowledge";

    /// <summary>Модель эмбеддингов MVP (Ollama).</summary>
    public string EmbeddingModel { get; set; } = "nomic-embed-text";

    /// <summary>
    /// Ожидаемая размерность вектора. Обязана совпадать с
    /// knowledge_chunks.embedding vector(768): несовпадение — исключение.
    /// </summary>
    public int EmbeddingDimensions { get; set; } = 768;

    /// <summary>Размер чанка в токенах (требование платформы — 500).</summary>
    public int ChunkSizeTokens { get; set; } = 500;

    /// <summary>Перекрытие соседних чанков в токенах (требование — 50).</summary>
    public int ChunkOverlapTokens { get; set; } = 50;

    /// <summary>topK поиска по умолчанию.</summary>
    public int DefaultTopK { get; set; } = 5;

    /// <summary>Порог косинусного сходства по умолчанию.</summary>
    public double DefaultThreshold { get; set; } = 0.77;

    /// <summary>
    /// Корень для относительных путей document-источников. null — content root хоста.
    /// </summary>
    public string? DocumentRoot { get; set; }

    /// <summary>Максимум страниц при обходе site (один уровень).</summary>
    public int SiteMaxPages { get; set; } = 20;

    /// <summary>Таймаут одного HTTP-запроса при инжесте site (требование — 5 с).</summary>
    public TimeSpan SiteRequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>User-Agent обходчика (учитывается в robots.txt).</summary>
    public string SiteUserAgent { get; set; } = "AgentPlatformKnowledge/1.0";

    /// <summary>
    /// In-memory реализация векторного хранилища: тесты и локальный запуск без pgvector.
    /// В production — false (pgvector).
    /// </summary>
    public bool UseInMemoryVectorStore { get; set; }

    /// <summary>Включить фоновую переиндексацию источников по cron из index_meta.</summary>
    public bool EnableScheduledReindex { get; set; } = true;

    /// <summary>Интервал опроса расписаний переиндексации.</summary>
    public TimeSpan ReindexPollInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Проверяет согласованность настроек (fail-fast при старте).</summary>
    public void Validate()
    {
        if (EmbeddingDimensions <= 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}:EmbeddingDimensions must be positive (nomic-embed-text = 768).");
        }

        if (ChunkSizeTokens <= 0)
        {
            throw new InvalidOperationException($"{SectionName}:ChunkSizeTokens must be positive.");
        }

        if (ChunkOverlapTokens < 0 || ChunkOverlapTokens >= ChunkSizeTokens)
        {
            throw new InvalidOperationException(
                $"{SectionName}:ChunkOverlapTokens must be in [0, ChunkSizeTokens).");
        }

        if (SiteMaxPages <= 0)
        {
            throw new InvalidOperationException($"{SectionName}:SiteMaxPages must be positive.");
        }

        if (SiteRequestTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{SectionName}:SiteRequestTimeout must be positive.");
        }

        if (DefaultTopK <= 0)
        {
            throw new InvalidOperationException($"{SectionName}:DefaultTopK must be positive.");
        }

        if (DefaultThreshold is < 0 or > 1)
        {
            throw new InvalidOperationException($"{SectionName}:DefaultThreshold must be in [0, 1].");
        }
    }
}
