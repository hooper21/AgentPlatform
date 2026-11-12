using AgentPlatform.Core.Contracts;
using AgentPlatform.LlmGateway;
using Microsoft.Extensions.AI;

namespace AgentPlatform.Gateway.Resilience;

/// <summary>
/// Фабрика клиентов модели с повторами: оборачивает фабрику LLM Gateway
/// (<see cref="IChatClientFactory"/>), не меняя её контракт.
/// </summary>
public sealed class RetryingChatClientFactory(
    IChatClientFactory inner,
    LlmResilienceOptions options,
    ILogger<RetryingChatClient> logger) : IChatClientFactory
{
    private readonly IChatClientFactory _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly LlmResilienceOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<RetryingChatClient> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public IChatClient Create(AgentModelRef modelRef)
    {
        ArgumentNullException.ThrowIfNull(modelRef);

        return new RetryingChatClient(_inner.Create(modelRef), _options, _logger);
    }
}
