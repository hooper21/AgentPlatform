namespace AgentPlatform.Knowledge.Ingest.Providers.Confluence;

/// <summary>
/// Настройки плагина Confluence: пагинация, таймауты и политика повторов.
/// <para>
/// Rate limit Atlassian (points-based для Cloud, burst — для API-токенов):
/// при 429/5xx запросы повторяются с экспоненциальным backoff и jitter,
/// задержка из Retry-After имеет приоритет. Заголовки X-RateLimit-Remaining /
/// X-RateLimit-NearLimit логируются.
/// TODO: Redis-очередь и распределённый rate-limit на несколько реплик.
/// </para>
/// </summary>
public sealed class ConfluenceOptions
{
    /// <summary>Секция конфигурации (Knowledge:Confluence).</summary>
    public const string SectionName = "Knowledge:Confluence";

    /// <summary>Размер страницы API (Cloud максимум 250, Server — 100).</summary>
    public int PageSize { get; set; } = 100;

    /// <summary>Предохранитель от бесконечной выгрузки одного пространства.</summary>
    public int MaxPagesPerSpace { get; set; } = 10_000;

    /// <summary>Максимум пространств в списке мастера (защита от гигантских инсталляций).</summary>
    public int MaxSpaces { get; set; } = 1_000;

    /// <summary>Таймаут одного HTTP-запроса.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Сколько раз повторять запрос при 429/5xx.</summary>
    public int MaxRetries { get; set; } = 4;

    /// <summary>Базовая задержка экспоненциального backoff.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Верхняя граница задержки backoff.</summary>
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Доля jitter от задержки (0..1), чтобы ретраи не совпадали.</summary>
    public double RetryJitterFactor { get; set; } = 0.2;

    /// <summary>
    /// Перекрытие окна инкремента: защита от гонки «страница изменена во время
    /// синхронизации». Уже применённые страницы повторно не индексируются.
    /// </summary>
    public TimeSpan ModifiedOverlap { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>User-Agent платформы (не секрет).</summary>
    public string UserAgent { get; set; } = "AgentPlatformKnowledge/1.0";

    /// <summary>Корень Atlassian API для OAuth 2.0 (3LO).</summary>
    public string CloudApiRoot { get; set; } = "https://api.atlassian.com";

    /// <summary>Проверяет настройки (fail-fast при старте сервиса).</summary>
    public void Validate()
    {
        if (PageSize <= 0)
        {
            throw new InvalidOperationException($"{SectionName}:PageSize must be positive.");
        }

        if (MaxPagesPerSpace <= 0)
        {
            throw new InvalidOperationException($"{SectionName}:MaxPagesPerSpace must be positive.");
        }

        if (MaxSpaces <= 0)
        {
            throw new InvalidOperationException($"{SectionName}:MaxSpaces must be positive.");
        }

        if (RequestTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{SectionName}:RequestTimeout must be positive.");
        }

        if (MaxRetries < 0)
        {
            throw new InvalidOperationException($"{SectionName}:MaxRetries must be >= 0.");
        }

        if (RetryBaseDelay < TimeSpan.Zero || RetryMaxDelay < RetryBaseDelay)
        {
            throw new InvalidOperationException(
                $"{SectionName}:RetryMaxDelay must be >= RetryBaseDelay >= 0.");
        }

        if (RetryJitterFactor is < 0 or > 1)
        {
            throw new InvalidOperationException($"{SectionName}:RetryJitterFactor must be in [0, 1].");
        }

        if (ModifiedOverlap < TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{SectionName}:ModifiedOverlap must be >= 0.");
        }

        if (!Uri.TryCreate(CloudApiRoot, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException($"{SectionName}:CloudApiRoot must be an absolute URL.");
        }
    }
}
