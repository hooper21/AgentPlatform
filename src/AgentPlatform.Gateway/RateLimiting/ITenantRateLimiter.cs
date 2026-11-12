namespace AgentPlatform.Gateway.RateLimiting;

/// <summary>Решение rate-limit по одному запросу.</summary>
/// <param name="Allowed">true — запрос можно обработать.</param>
/// <param name="Remaining">Сколько токенов осталось в ведре (целая часть).</param>
/// <param name="RetryAfter">Через сколько повторить запрос (для 429).</param>
public sealed record RateLimitDecision(bool Allowed, int Remaining, TimeSpan RetryAfter)
{
    /// <summary>Решение «пропустить» (fail-open при недоступном хранилище).</summary>
    public static RateLimitDecision Allow { get; } = new(true, 0, TimeSpan.Zero);
}

/// <summary>
/// Rate-limit на тенанта (token bucket). Лимит проверяется до запуска агента,
/// чтобы недоступность LLM не расходовала бюджет тенанта.
/// </summary>
public interface ITenantRateLimiter
{
    /// <summary>
    /// Занимает один токен ведра тенанта. Реализация не бросает исключений при
    /// недоступном Redis: лимит деградирует в fail-open (доступность важнее).
    /// </summary>
    Task<RateLimitDecision> AcquireAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
