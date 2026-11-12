using System.Runtime.CompilerServices;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Contracts;
using AgentPlatform.LlmGateway;
using Microsoft.Extensions.AI;

namespace AgentPlatform.Tests.Runtime;

/// <summary>
/// Сценарный IChatClient: тест задаёт очередь ответов модели (текст или вызов тула).
/// Сохраняет переданную историю, чтобы проверить, что диалог продолжается после
/// рестарта процесса (история приходит из чекпоинта, а не из памяти).
/// </summary>
internal sealed class ScriptedChatClient : IChatClient
{
    private readonly Queue<Func<IReadOnlyList<ChatMessage>, ChatResponse>> _scripts = new();

    public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

    public List<ChatOptions?> ToolOptions { get; } = [];

    public ScriptedChatClient EnqueueReply(string text)
    {
        _scripts.Enqueue(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        return this;
    }

    public ScriptedChatClient EnqueueToolCall(string toolName, IDictionary<string, object?>? arguments = null)
    {
        var call = new FunctionCallContent(
            Guid.NewGuid().ToString("N"),
            toolName,
            arguments ?? new Dictionary<string, object?>(StringComparer.Ordinal));

        _scripts.Enqueue(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
        return this;
    }

    public ScriptedChatClient EnqueueError(string message)
    {
        _scripts.Enqueue(_ => throw new InvalidOperationException(message));
        return this;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var history = messages.ToList();
        Calls.Add(history);
        ToolOptions.Add(options);

        if (_scripts.Count == 0)
        {
            throw new InvalidOperationException("ScriptedChatClient has no scripted response left.");
        }

        return Task.FromResult(_scripts.Dequeue()(history));
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

/// <summary>Фабрика клиента модели: всегда отдаёт подготовленный ScriptedChatClient.</summary>
internal sealed class SingleChatClientFactory(IChatClient chatClient) : IChatClientFactory
{
    public IChatClient Create(AgentModelRef modelRef) => chatClient;
}

/// <summary>Конфигурации агента в памяти + счётчик загрузок (для проверки кеша).</summary>
internal sealed class FakeAgentConfigStore : IAgentConfigStore
{
    private readonly Dictionary<(Guid TenantId, Guid AgentId, int Version), AgentRuntimeConfig> _configs = [];

    public int Loads { get; private set; }

    public FakeAgentConfigStore(params AgentRuntimeConfig[] configs)
    {
        foreach (var config in configs)
        {
            _configs[(config.TenantId, config.AgentId, config.Version)] = config;
        }
    }

    public void Set(AgentRuntimeConfig config) => _configs[(config.TenantId, config.AgentId, config.Version)] = config;

    public void Remove(AgentRuntimeConfig config) => _configs.Remove((config.TenantId, config.AgentId, config.Version));

    public Task<AgentRuntimeConfig?> GetAsync(
        Guid tenantId,
        Guid agentId,
        int agentVersion,
        CancellationToken cancellationToken = default)
    {
        Loads++;

        return Task.FromResult(
            _configs.TryGetValue((tenantId, agentId, agentVersion), out var config) ? config : null);
    }
}

/// <summary>Обёртка над инвокатором тулов: запоминает фактические вызовы.</summary>
internal sealed class RecordingToolInvoker(IToolInvoker inner) : IToolInvoker
{
    public List<ToolInvocationRequest> Requests { get; } = [];

    public Task<ToolInvocationResult> InvokeAsync(
        ToolInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return inner.InvokeAsync(request, cancellationToken);
    }
}

/// <summary>Хранилище чекпоинтов в памяти — для проверки DI-регистрации Runtime.</summary>
internal sealed class InMemoryCheckpointStore : ICheckpointStore
{
    private readonly List<SessionCheckpoint> _checkpoints = [];

    public Task<SessionCheckpoint?> GetLatestAsync(
        Guid tenantId,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var latest = _checkpoints
            .Where(checkpoint => checkpoint.TenantId == tenantId && checkpoint.SessionId == sessionId)
            .OrderBy(checkpoint => checkpoint.Superstep)
            .LastOrDefault();

        return Task.FromResult(latest);
    }

    public Task SaveAsync(SessionCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        _checkpoints.Add(checkpoint);
        return Task.CompletedTask;
    }
}

