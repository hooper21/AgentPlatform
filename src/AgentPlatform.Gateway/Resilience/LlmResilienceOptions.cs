namespace AgentPlatform.Gateway.Resilience;

/// <summary>
/// Настройки устойчивости к недоступности LLM (секция <c>Gateway:Llm</c>).
/// <para>
/// Ретраи выполняются на уровне клиента модели, а не вокруг хода оркестратора:
/// повторный RunAsync добавил бы сообщение визитера в чекпоинт второй раз.
/// </para>
/// </summary>
public sealed class LlmResilienceOptions
{
    /// <summary>Fallback-сообщение визитеру, когда модель недоступна после всех попыток.</summary>
    public const string DefaultFallbackReply =
        "Извините, сейчас я не могу ответить — менеджер ответит лично.";

    /// <summary>Количество повторов вызова модели (всего попыток — Retries + 1).</summary>
    public int Retries { get; set; } = 2;

    /// <summary>Пауза между попытками.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Fallback-сообщение при деградации (сессия помечается degraded).</summary>
    public string FallbackReply { get; set; } = DefaultFallbackReply;

    /// <summary>Проверяет настройки.</summary>
    public void Validate()
    {
        if (Retries < 0)
        {
            throw new InvalidOperationException("Gateway:Llm:Retries must not be negative.");
        }

        if (RetryDelay < TimeSpan.Zero)
        {
            throw new InvalidOperationException("Gateway:Llm:RetryDelay must not be negative.");
        }

        if (string.IsNullOrWhiteSpace(FallbackReply))
        {
            throw new InvalidOperationException("Gateway:Llm:FallbackReply must not be empty.");
        }
    }
}
