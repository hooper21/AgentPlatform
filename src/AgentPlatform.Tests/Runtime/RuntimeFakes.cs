using System.Runtime.CompilerServices;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;
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

    /// <summary>Сценарный ответ, зависящий от фактической истории сообщений.</summary>
    public ScriptedChatClient Enqueue(Func<IReadOnlyList<ChatMessage>, ChatResponse> script)
    {
        ArgumentNullException.ThrowIfNull(script);
        _scripts.Enqueue(script);
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

/// <summary>
/// Хранилище заявок approvals в памяти — для проверки DI-регистрации Runtime
/// и unit-тестов без БД. Повторяет контракт <see cref="IApprovalStore"/>.
/// </summary>
internal sealed class InMemoryApprovalStore : IApprovalStore
{
    private readonly Dictionary<Guid, ApprovalRequest> _approvals = [];

    public Task<ApprovalRequest> CreateAsync(
        ApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stored = request with
        {
            Id = request.Id == Guid.Empty ? Guid.NewGuid() : request.Id,
            RequestedAt = request.RequestedAt == default ? DateTimeOffset.UtcNow : request.RequestedAt
        };

        _approvals[stored.Id] = stored;
        return Task.FromResult(stored);
    }

    public Task<ApprovalRequest?> FindAsync(
        Guid tenantId,
        Guid approvalId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(
            _approvals.TryGetValue(approvalId, out var approval) && approval.TenantId == tenantId
                ? approval
                : null);

    public Task<ApprovalRequest?> FindPendingAsync(
        Guid tenantId,
        Guid sessionId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(_approvals.Values
            .Where(approval => approval.TenantId == tenantId
                && approval.SessionId == sessionId
                && approval.Status == ApprovalStatus.Pending)
            .OrderByDescending(approval => approval.RequestedAt)
            .FirstOrDefault());

    public Task<ApprovalRequest> DecideAsync(
        Guid tenantId,
        Guid approvalId,
        Decision decision,
        CancellationToken cancellationToken = default)
    {
        var approval = _approvals.TryGetValue(approvalId, out var found) && found.TenantId == tenantId
            ? found
            : throw new InvalidOperationException($"Approval {approvalId} is not found for tenant {tenantId}.");

        if (approval.Status != ApprovalStatus.Pending)
        {
            throw new InvalidOperationException(
                $"Approval {approvalId} is already decided (status '{approval.Status}').");
        }

        var decided = approval with
        {
            Status = decision.Status,
            DecidedBy = decision.DecidedBy,
            DecidedByKind = decision.DecidedByKind,
            Comment = decision.Comment,
            DecidedAt = decision.DecidedAt ?? DateTimeOffset.UtcNow
        };

        _approvals[approvalId] = decided;
        return Task.FromResult(decided);
    }

    public Task<ApprovalRequest?> CancelPendingAsync(
        Guid tenantId,
        Guid sessionId,
        string? comment = null,
        CancellationToken cancellationToken = default)
    {
        var approval = _approvals.Values
            .Where(candidate => candidate.TenantId == tenantId
                && candidate.SessionId == sessionId
                && candidate.Status == ApprovalStatus.Pending)
            .OrderByDescending(candidate => candidate.RequestedAt)
            .FirstOrDefault();

        if (approval is null)
        {
            return Task.FromResult<ApprovalRequest?>(null);
        }

        var cancelled = approval with
        {
            Status = ApprovalStatus.Superseded,
            DecidedByKind = ApprovalDecisionKind.System,
            Comment = comment,
            DecidedAt = DateTimeOffset.UtcNow
        };

        _approvals[cancelled.Id] = cancelled;
        return Task.FromResult<ApprovalRequest?>(cancelled);
    }
}

