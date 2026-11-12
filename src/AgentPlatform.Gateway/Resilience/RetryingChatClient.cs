using Microsoft.Extensions.AI;

namespace AgentPlatform.Gateway.Resilience;

/// <summary>
/// Клиент модели с повторами: недоступность LLM (исключение провайдера, таймаут)
/// повторяется <see cref="LlmResilienceOptions.Retries"/> раз, после чего исключение
/// уходит в Runtime. Runtime фиксирует неудачный ход (failed_turns) и возвращает
/// итог degraded — Gateway отвечает fallback-сообщением и помечает сессию degraded.
/// </summary>
public sealed class RetryingChatClient(
    IChatClient inner,
    LlmResilienceOptions options,
    ILogger<RetryingChatClient> logger) : IChatClient
{
    private readonly IChatClient _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly LlmResilienceOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<RetryingChatClient> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        // IEnumerable может быть однократным: повторные попытки используют тот же список.
        var history = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var attempts = _options.Retries + 1;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _inner
                    .GetResponseAsync(history, chatOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (attempt < attempts)
            {
                _logger.LogWarning(
                    exception,
                    "LLM call failed (attempt {Attempt}/{Attempts}), retrying",
                    attempt,
                    attempts);

                if (_options.RetryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_options.RetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
    {
        // TODO(2.x): повтор стрима до первого чанка, когда Runtime научится отдавать поток.
        return _inner.GetStreamingResponseAsync(messages, chatOptions, cancellationToken);
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceKey is null && serviceType.IsInstanceOfType(this)
            ? this
            : _inner.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc />
    public void Dispose() => _inner.Dispose();
}
