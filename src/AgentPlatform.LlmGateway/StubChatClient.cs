using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentPlatform.LlmGateway;

/// <summary>
/// Заглушка IChatClient: возвращает детерминированный ответ без вызова внешних API.
/// Нужна, чтобы сервисы стартовали без ключей провайдеров; реальные провайдеры
/// подключаются в задачах LLM Gateway (фаза 2).
/// </summary>
public sealed class StubChatClient(string model = "platform-default") : IChatClient
{
    private readonly string _model = model;

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var lastMessage = messages.LastOrDefault()?.Text ?? string.Empty;
        var response = new ChatResponse(
            new ChatMessage(ChatRole.Assistant, $"[stub:{_model}] {lastMessage}"));

        return Task.FromResult(response);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        // TODO(2.x): потоковая передача от реального провайдера через IChatClient.
        yield return new ChatResponseUpdate(ChatRole.Assistant, $"[stub:{_model}] streaming is not implemented yet");
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Заглушка не держит внешних ресурсов.
    }
}
