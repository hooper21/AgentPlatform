using AgentPlatform.Core.Contracts;
using Microsoft.Extensions.AI;

namespace AgentPlatform.LlmGateway;

/// <summary>
/// Обёртка над IChatClient платформы: единая точка для выбора модели тенанта,
/// учёта токенов и OTel-трейсинга (реализация — задачи фазы 2).
/// </summary>
public sealed class PlatformChatClient(IChatClient inner) : IChatClient
{
    private readonly IChatClient _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // TODO(2.x): учёт usage (tokens_in/tokens_out), маскирование ПДн, OTel-спан.
        return _inner.GetResponseAsync(messages, options, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => _inner.GetStreamingResponseAsync(messages, options, cancellationToken);

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is null && serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        return _inner.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc />
    public void Dispose() => _inner.Dispose();
}

/// <summary>Фабрика IChatClient под конкретную модель агента/тенанта.</summary>
public interface IChatClientFactory
{
    /// <summary>Возвращает клиента для указанной модели.</summary>
    IChatClient Create(AgentModelRef modelRef);
}

/// <summary>
/// Заглушка фабрики: всегда отдаёт <see cref="StubChatClient"/>.
/// TODO(2.x): платформенный провайдер и собственные endpoint-ы тенантов (llm_config.mode = own).
/// </summary>
public sealed class StubChatClientFactory : IChatClientFactory
{
    /// <inheritdoc />
    public IChatClient Create(AgentModelRef modelRef)
    {
        ArgumentNullException.ThrowIfNull(modelRef);

        var client = new StubChatClient(modelRef.Model);
        return new PlatformChatClient(client);
    }
}

/// <summary>Настройки LLM Gateway (секреты — только по ссылке на Secret Service).</summary>
public sealed class LlmGatewayOptions
{
    /// <summary>Модель по умолчанию для платформенных тарифов.</summary>
    public string DefaultModel { get; set; } = "platform-default";

    /// <summary>Endpoint платформенного провайдера.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Ссылка на секрет платформенного провайдера.</summary>
    public string? ApiKeyRef { get; set; }
}
