using System.Runtime.CompilerServices;
using AgentPlatform.Gateway.Security;
using Microsoft.Extensions.AI;

namespace AgentPlatform.Tests.Gateway;

/// <summary>Секреты виджета для тестов проверки JWT (без конфигурации/Secret Service).</summary>
internal sealed class FixedWidgetSecretProvider(params (Guid TenantId, string Secret)[] secrets) : IWidgetSecretProvider
{
    private readonly Dictionary<Guid, string> _secrets =
        secrets.ToDictionary(pair => pair.TenantId, pair => pair.Secret);

    public string? GetSecret(Guid tenantId)
        => _secrets.TryGetValue(tenantId, out var secret) ? secret : null;
}

/// <summary>LLM, которая всегда недоступна: считает попытки, чтобы проверить ретраи.</summary>
internal sealed class FailingChatClient : IChatClient
{
    public int Attempts { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        Attempts++;

        throw new InvalidOperationException("LLM provider is unavailable.");
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
