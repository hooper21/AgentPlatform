using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentPlatform.ConnectorHost.Tools;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Json;
using AgentPlatform.Gateway.Composition;
using AgentPlatform.Gateway.Contracts;
using AgentPlatform.Gateway.RateLimiting;
using AgentPlatform.Gateway.Resilience;
using AgentPlatform.Gateway.Sessions;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Persistence.Entities;
using AgentPlatform.Infrastructure.Persistence.Interceptors;
using AgentPlatform.Infrastructure.Repositories;
using AgentPlatform.Infrastructure.Tenancy;
using AgentPlatform.LlmGateway;
using AgentPlatform.Runtime;
using AgentPlatform.Tests.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPlatform.Tests.Gateway;

/// <summary>
/// Стенд интеграционного теста Gateway: одна InMemory-БД с тенантом, виджет-каналом,
/// агентом и коннектором EchoTools; DI собирает те же сервисы, что и Program.cs,
/// подменяя только LLM-клиент (сценарный) и транспорт тулов (in-process EchoTools).
/// </summary>
internal sealed class GatewayHarness : IDisposable
{
    public static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid AgentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid ChannelId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid InstallationId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid RegistryId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    /// <summary>Секрет подписи JWT виджета тестового тенанта (HS256).</summary>
    public const string WidgetSecret = "test-widget-secret-0123456789abcdef";

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly ServiceProvider _provider;
    private readonly string _databaseName = $"agentplatform-gateway-{Guid.NewGuid():N}";

    public GatewayHarness(
        IChatClient chatClient,
        LlmResilienceOptions? resilience = null,
        RateLimitOptions? rateLimit = null)
    {
        ChatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));

        Resilience = resilience ?? new LlmResilienceOptions { Retries = 2, RetryDelay = TimeSpan.Zero };
        Resilience.Validate();

        var limits = rateLimit ?? new RateLimitOptions { Storage = "memory", Capacity = 100, TokensPerSecond = 100 };
        limits.Validate();

        ToolInvoker = new RecordingToolInvoker(new EchoToolsInvoker());

        var configuration = BuildConfiguration(limits, Resilience);
        var services = BuildServices(configuration);

        _provider = services.BuildServiceProvider();

        Seed();
    }

    public IChatClient ChatClient { get; }

    public LlmResilienceOptions Resilience { get; }

    public RecordingToolInvoker ToolInvoker { get; }

    /// <summary>SSE-поток Gateway (тот же экземпляр, что у endpoint-ов).</summary>
    public ReplyStreamHub StreamHub => _provider.GetRequiredService<ReplyStreamHub>();

    /// <summary>JWT виджета тестового тенанта.</summary>
    public string CreateToken(string? userRef = null, Guid? tenantId = null, string? secret = null, TimeSpan? lifetime = null)
        => CreateWidgetToken(tenantId ?? TenantId, secret ?? WidgetSecret, userRef, lifetime);

    /// <summary>Отправляет сообщение от нового визитера (без cookie/localStorage).</summary>
    public Task<MessageTurn> SendAsync(IncomingChannelMessage message)
        => SendAsync(new ChannelRequest(CreateToken()), message);

    /// <summary>Отправляет сообщение с заданными данными канала (cookie/localStorage/токен).</summary>
    public async Task<MessageTurn> SendAsync(ChannelRequest request, IncomingChannelMessage message)
    {
        using var scope = _provider.CreateScope();

        var pipeline = scope.ServiceProvider.GetRequiredService<GatewayMessagePipeline>();

        return await pipeline
            .AcceptAsync(TenantId.ToString("D"), request, message, CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>Читает события SSE-потока сессии (поток закрыт событием done).</summary>
    public async Task<IReadOnlyList<ReplyStreamEvent>> ReadStreamAsync(Guid sessionId)
    {
        var events = new List<ReplyStreamEvent>();

        await foreach (var streamEvent in StreamHub.ReadAsync(sessionId).ConfigureAwait(false))
        {
            events.Add(streamEvent);
        }

        return events;
    }

    /// <summary>
    /// Проверочные данные: тенант, виджет-канал, коннектор EchoTools (манифест +
    /// установка + tool_settings) и агент с включёнными echo_read/echo_write.
    /// </summary>
    private void Seed()
    {
        using var context = CreateContext();

        context.Tenants.Add(new Tenant
        {
            Id = TenantId,
            Name = "Демо Тенант",
            Plan = "pilot",
            LlmConfig = "{}",
            Status = "active",
            CreatedAt = Now,
            UpdatedAt = Now
        });

        context.Channels.Add(new Channel
        {
            Id = ChannelId,
            TenantId = TenantId,
            Type = "widget",
            Config = "{}",
            Status = "active",
            CreatedAt = Now,
            UpdatedAt = Now
        });

        var manifest = EchoToolsManifest.Create();

        context.ConnectorRegistry.Add(new ConnectorRegistryEntry
        {
            Id = RegistryId,
            Name = manifest.Name,
            Version = manifest.Version,
            Manifest = PlatformJson.SerializeDb(manifest),
            ManifestHash = new string('a', 64),
            Publisher = "platform",
            Status = "published",
            CreatedAt = Now,
            UpdatedAt = Now
        });

        context.ConnectorInstallations.Add(new ConnectorInstallation
        {
            Id = InstallationId,
            TenantId = TenantId,
            RegistryId = RegistryId,
            CredentialsRef = "secret://echo",
            AuthType = "external_mcp",
            Status = "active",
            CreatedAt = Now,
            UpdatedAt = Now
        });

        // Read-тул без гейта: read-вызовы выполняются сразу; write идёт через HITL.
        context.ToolSettings.Add(new ToolSetting
        {
            Id = Guid.NewGuid(),
            InstallationId = InstallationId,
            ToolName = EchoToolsManifest.ReadTool,
            Enabled = true,
            Policy = "read",
            PolicySource = "tenant_override",
            GateSettings = """{"mode":"never"}""",
            CreatedAt = Now,
            UpdatedAt = Now
        });

        context.Agents.Add(new Agent
        {
            Id = AgentId,
            TenantId = TenantId,
            Name = "echo-agent",
            CurrentVersion = 1,
            CreatedAt = Now,
            UpdatedAt = Now
        });

        context.AgentVersions.Add(new AgentVersion
        {
            Id = Guid.NewGuid(),
            AgentId = AgentId,
            Version = 1,
            Status = "published",
            SystemPrompt = "Ты тестовый агент платформы.",
            ModelRef = """{"model":"test-model","routing":"simple"}""",
            EnabledTools =
                $$"""[{"installation_id":"{{InstallationId}}","tool_name":"{{EchoToolsManifest.ReadTool}}","policy":"read"},{"installation_id":"{{InstallationId}}","tool_name":"{{EchoToolsManifest.WriteTool}}","policy":"write"}]""",
            Rules = "{}",
            EscalationRules = """{"failed_turns":3}""",
            Limits = """{"max_turns":10,"max_tool_calls":8}""",
            CreatedAt = Now
        });

        context.SaveChanges();
    }

    /// <summary>Новый DbContext над той же InMemory-БД (проверка состояния после хода).</summary>
    public AgentPlatformDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AgentPlatformDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(new AuditingInterceptor())
            .Options;

        var context = new AgentPlatformDbContext(options);
        context.Database.EnsureCreated();

        return context;
    }

    /// <summary>Сессия из БД (null — сессии нет).</summary>
    public AgentSession? FindSession(Guid sessionId)
    {
        using var context = CreateContext();

        return context.Sessions
            .AsNoTracking()
            .SingleOrDefault(session => session.Id == sessionId);
    }

    /// <summary>Сообщения журнала диалога сессии в порядке записи.</summary>
    public IReadOnlyList<Message> FindMessages(Guid sessionId)
    {
        using var context = CreateContext();

        return context.Messages
            .AsNoTracking()
            .Where(message => message.SessionId == sessionId)
            .OrderBy(message => message.Ts)
            .ThenBy(message => message.Id)
            .ToList();
    }

    public void Dispose() => _provider.Dispose();

    /// <summary>JWT виджета: HS256, claim tenant_key/exp/user_ref.</summary>
    internal static string CreateWidgetToken(
        Guid tenantId,
        string secret,
        string? userRef = null,
        TimeSpan? lifetime = null)
    {
        var header = Base64UrlEncode(Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}"""));

        var claims = new Dictionary<string, object>
        {
            ["tenant_key"] = tenantId.ToString("D"),
            ["exp"] = DateTimeOffset.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(5)).ToUnixTimeSeconds()
        };

        if (!string.IsNullOrWhiteSpace(userRef))
        {
            claims["user_ref"] = userRef;
        }

        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(claims));

        return $"{header}.{payload}.{Sign($"{header}.{payload}", secret)}";
    }

    private static string Sign(string data, string secret)
        => Base64UrlEncode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(data)));

    private static string Base64UrlEncode(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static IConfiguration BuildConfiguration(RateLimitOptions rateLimit, LlmResilienceOptions resilience)
    {
        var values = new Dictionary<string, string?>
        {
            ["Gateway:Widget:Secrets:" + TenantId.ToString("D")] = WidgetSecret,
            ["Gateway:RateLimit:Storage"] = rateLimit.Storage,
            ["Gateway:RateLimit:Capacity"] = rateLimit.Capacity.ToString(CultureInfo.InvariantCulture),
            ["Gateway:RateLimit:TokensPerSecond"] = rateLimit.TokensPerSecond.ToString(CultureInfo.InvariantCulture),
            ["Gateway:RateLimit:Cost"] = rateLimit.Cost.ToString(CultureInfo.InvariantCulture),
            ["Gateway:RateLimit:KeyTtl"] = rateLimit.KeyTtl.ToString(),
            ["Gateway:RateLimit:KeyPrefix"] = rateLimit.KeyPrefix,
            ["Gateway:Llm:Retries"] = resilience.Retries.ToString(CultureInfo.InvariantCulture),
            ["Gateway:Llm:RetryDelay"] = resilience.RetryDelay.ToString(),
            ["Gateway:Llm:FallbackReply"] = resilience.FallbackReply
        };

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private IServiceCollection BuildServices(IConfiguration configuration)
    {
        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();

        services.AddDbContext<AgentPlatformDbContext>(options => options
            .UseInMemoryDatabase(_databaseName)
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(new AuditingInterceptor()));

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(provider => provider.GetRequiredService<TenantContext>());
        services.AddScoped(typeof(ITenantScopedRepository<>), typeof(TenantScopedRepository<>));
        services.AddScoped(typeof(IGlobalRepository<>), typeof(GlobalRepository<>));

        services.AddScoped<IAgentConfigStore>(provider =>
            new EfAgentConfigStore(provider.GetRequiredService<AgentPlatformDbContext>()));
        services.AddScoped<ICheckpointStore>(provider =>
            new EfCheckpointStore(provider.GetRequiredService<AgentPlatformDbContext>()));
        services.AddScoped<IApprovalStore>(provider =>
            new EfApprovalStore(provider.GetRequiredService<AgentPlatformDbContext>()));

        services.AddSingleton(ToolInvoker);
        services.AddSingleton<IToolInvoker>(ToolInvoker);

        services.AddMafAgentRuntime(new AgentRuntimeOptions());

        // Gateway: JWT, rate-limit, сессии, SSE. LLM подменяется сценарным клиентом.
        services.AddGatewaySessions(configuration);
        services.AddSingleton(Resilience);
        services.AddSingleton<IChatClientFactory>(new RetryingChatClientFactory(
            new SingleChatClientFactory(ChatClient),
            Resilience,
            NullLogger<RetryingChatClient>.Instance));

        return services;
    }
}

