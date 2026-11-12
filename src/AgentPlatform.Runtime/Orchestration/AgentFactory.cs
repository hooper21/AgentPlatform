using System.Text.Json;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Dto;
using AgentPlatform.LlmGateway;
using Microsoft.Extensions.AI;

namespace AgentPlatform.Runtime.Orchestration;

/// <summary>
/// AgentFactory: собирает агента из конфигурации тенанта — системный промпт, модель
/// и активные тулы подключённых коннекторов (по манифесту и tool_settings).
/// <para>
/// Version pinning: конфигурация запрашивается строго для
/// <see cref="SessionContext.AgentVersion"/> (версия, с которой начата сессия), а не
/// для agents.current_version. Публикация новой версии не меняет поведение
/// идущего диалога и не ломает resume.
/// </para>
/// <para>
/// Конфигурация кешируется в Redis с TTL 60 с; при изменении конфигурации
/// (<see cref="InvalidateAsync"/>) запись инвалидируется.
/// </para>
/// <para>
/// Клиент модели создаётся фабрикой LLM Gateway на каждый ход; владение им
/// передаётся Runtime. Реальная фабрика (LLM Gateway, 2.x) отдаёт пул/синглтон
/// клиентов и сама управляет их закрытием.
/// </para>
/// </summary>
public sealed class AgentFactory(
    IAgentConfigStore configStore,
    IAgentConfigCache configCache,
    IChatClientFactory chatClientFactory,
    AgentRuntimeOptions options)
{
    private const string FallbackInputSchema = """{"type":"object","properties":{}}""";

    private readonly IAgentConfigStore _configStore =
        configStore ?? throw new ArgumentNullException(nameof(configStore));

    private readonly IAgentConfigCache _configCache =
        configCache ?? throw new ArgumentNullException(nameof(configCache));

    private readonly IChatClientFactory _chatClientFactory =
        chatClientFactory ?? throw new ArgumentNullException(nameof(chatClientFactory));

    private readonly AgentRuntimeOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Собирает агента закреплённой версии сессии (кеш → БД).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Агент/версия не найдены или конфигурация не соответствует сессии.
    /// </exception>
    public async Task<PlatformAgent> CreateAsync(SessionContext session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.AgentVersion <= 0)
        {
            throw new ArgumentException("session.agent_version must be positive", nameof(session));
        }

        var config = await _configCache
            .GetAsync(session.TenantId, session.AgentId, session.AgentVersion, cancellationToken)
            .ConfigureAwait(false);

        if (config is null)
        {
            config = await _configStore
                .GetAsync(session.TenantId, session.AgentId, session.AgentVersion, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Agent configuration is not available: tenant {session.TenantId}, " +
                    $"agent {session.AgentId}, version {session.AgentVersion}.");

            await _configCache.SetAsync(config, cancellationToken).ConfigureAwait(false);
        }

        ValidatePinning(session, config);

        var declarations = config.Tools
            .Select(CreateDeclaration)
            .ToList();

        var chatClient = _chatClientFactory.Create(config.Model);

        return new PlatformAgent(config, chatClient, declarations);
    }

    /// <summary>
    /// Инвалидирует кеш конфигурации агента. Вызывается при изменении конфигурации
    /// (сохранение версии, настроек тулов, установка/отключение коннектора).
    /// </summary>
    public Task InvalidateAsync(Guid tenantId, Guid agentId, CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("tenant_id is required", nameof(tenantId));
        }

        if (agentId == Guid.Empty)
        {
            throw new ArgumentException("agent_id is required", nameof(agentId));
        }

        return _configCache.InvalidateAsync(tenantId, agentId, cancellationToken);
    }

    /// <summary>TTL кеша конфигурации (60 с по умолчанию).</summary>
    public TimeSpan ConfigCacheTtl => _options.ConfigCacheTtl;

    private static void ValidatePinning(SessionContext session, AgentRuntimeConfig config)
    {
        if (config.TenantId != session.TenantId || config.AgentId != session.AgentId)
        {
            throw new InvalidOperationException(
                "Agent configuration does not belong to the session tenant/agent.");
        }

        if (config.Version != session.AgentVersion)
        {
            throw new InvalidOperationException(
                $"Version pinning violated: session pins agent version {session.AgentVersion}, " +
                $"but configuration has version {config.Version}.");
        }
    }

    private static AIFunctionDeclaration CreateDeclaration(AgentToolConfig tool)
    {
        JsonElement schema;

        try
        {
            using var document = JsonDocument.Parse(tool.InputSchema);
            schema = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Схема манифеста проверяется при установке коннектора (fail-safe здесь).
            using var fallback = JsonDocument.Parse(FallbackInputSchema);
            schema = fallback.RootElement.Clone();
        }

        var description = string.IsNullOrWhiteSpace(tool.Description)
            ? $"Tool {tool.ToolName}"
            : tool.Description;

        return AIFunctionFactory.CreateDeclaration(tool.ToolName, description, schema);
    }
}
