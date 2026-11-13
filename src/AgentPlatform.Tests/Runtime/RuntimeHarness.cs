using AgentPlatform.ConnectorHost.Tools;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Dto;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Persistence.Interceptors;
using AgentPlatform.Infrastructure.Repositories;
using AgentPlatform.Runtime;
using AgentPlatform.Runtime.Orchestration;
using AgentPlatform.Runtime.Policy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;

namespace AgentPlatform.Tests.Runtime;

/// <summary>
/// Тестовый стенд Runtime: одна InMemory-БД на стенд, но каждый
/// <see cref="CreateRuntime"/> создаёт новый DbContext, хранилище чекпоинтов, кеш и
/// AgentFactory — как новый процесс после рестарта.
/// </summary>
internal sealed class RuntimeHarness : IDisposable
{
    private readonly string _databaseName = $"agentplatform-runtime-{Guid.NewGuid():N}";
    private readonly List<AgentPlatformDbContext> _contexts = [];

    public RuntimeHarness(
        AgentRuntimeConfig config,
        ScriptedChatClient chatClient,
        IToolInvoker? toolInvoker = null,
        IAgentConfigStore? configStore = null,
        IKnowledgeContextProvider? knowledgeContextProvider = null)
    {
        Config = config;
        ChatClient = chatClient;
        ConfigStore = configStore ?? new FakeAgentConfigStore(config);
        ToolInvoker = new RecordingToolInvoker(toolInvoker ?? new EchoToolsInvoker());
        Options = new AgentRuntimeOptions();
        KnowledgeContextProvider = knowledgeContextProvider;
    }

    public AgentRuntimeConfig Config { get; }

    public ScriptedChatClient ChatClient { get; }

    public IAgentConfigStore ConfigStore { get; }

    public RecordingToolInvoker ToolInvoker { get; }

    public AgentRuntimeOptions Options { get; }

    /// <summary>RAG-порт Knowledge Service (null — знания не подключаются).</summary>
    public IKnowledgeContextProvider? KnowledgeContextProvider { get; }

    /// <summary>Создаёт новый DbContext над той же InMemory-БД.</summary>
    public AgentPlatformDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AgentPlatformDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(new AuditingInterceptor())
            .Options;

        var context = new AgentPlatformDbContext(options);
        context.Database.EnsureCreated();
        _contexts.Add(context);

        return context;
    }

    /// <summary>
    /// Поднимает новый экземпляр оркестратора (эмуляция старта процесса):
    /// новый DbContext + EfCheckpointStore + кеш конфигурации.
    /// </summary>
    public MafOrchestrationRuntime CreateRuntime(
        AgentPlatformDbContext? context = null,
        IAgentConfigCache? configCache = null)
    {
        var database = context ?? CreateContext();

        var agentFactory = new AgentFactory(
            ConfigStore,
            configCache ?? new InMemoryAgentConfigCache(Options.ConfigCacheTtl),
            new SingleChatClientFactory(ChatClient),
            Options);

        return new MafOrchestrationRuntime(
            agentFactory,
            new EfCheckpointStore(database),
            ToolInvoker,
            new PolicyEngine(),
            new EfApprovalStore(database),
            new AgentRuntimeGateTimeoutProvider(Options),
            Options,
            KnowledgeContextProvider);
    }

    /// <summary>Заявка approvals в отдельном DbContext (эмуляция чтения из БД).</summary>
    public ApprovalRequest? FindApproval(Guid approvalId)
        => new EfApprovalStore(CreateContext())
            .FindAsync(Config.TenantId, approvalId)
            .GetAwaiter()
            .GetResult();

    /// <summary>Ожидающая заявка сессии (null — сессия не ждёт подтверждения).</summary>
    public ApprovalRequest? FindPendingApproval(Guid sessionId)
        => new EfApprovalStore(CreateContext())
            .FindPendingAsync(Config.TenantId, sessionId)
            .GetAwaiter()
            .GetResult();

    /// <summary>Контекст сессии с закреплённой версией агента.</summary>
    public SessionContext CreateSession(int agentVersion = EchoAgentConfig.Version)
        => new(
            Guid.NewGuid(),
            Config.TenantId,
            Config.AgentId,
            agentVersion,
            Guid.NewGuid(),
            "visitor-1");

    /// <summary>Входящее сообщение визитера для сессии.</summary>
    public IncomingMessage CreateMessage(SessionContext session, string text)
        => new(Config.TenantId, session.ChannelId, session.UserRef, text);

    /// <summary>Состояние последнего чекпоинта сессии (нейтральный контракт).</summary>
    public SessionState LatestState(Guid sessionId)
    {
        var checkpoint = LatestCheckpoint(sessionId);
        return StateContract.DeserializeRequired(checkpoint.State);
    }

    /// <summary>Последний чекпоинт сессии.</summary>
    public SessionCheckpoint LatestCheckpoint(Guid sessionId)
        => new EfCheckpointStore(CreateContext())
            .GetLatestAsync(Config.TenantId, sessionId)
            .GetAwaiter()
            .GetResult()
            ?? throw new InvalidOperationException($"Session {sessionId} has no checkpoint.");

    public void Dispose()
    {
        foreach (var context in _contexts)
        {
            context.Dispose();
        }
    }
}
