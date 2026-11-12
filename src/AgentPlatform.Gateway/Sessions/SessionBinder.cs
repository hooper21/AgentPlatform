using System.Text.Json;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Enums;
using AgentPlatform.Gateway.Errors;
using AgentPlatform.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Gateway.Sessions;

/// <summary>
/// Привязка сообщения канала к сессии платформы (таблица sessions).
/// <para>
/// Канал о сессиях не знает (контракт IChannelAdapter): Gateway по user_ref находит
/// открытую сессию визитера или создаёт новую. Агент и его версия фиксируются на
/// старте сессии (version pinning) и не меняются при следующих сообщениях.
/// </para>
/// <para>
/// Все запросы идут через tenant-фильтрованные репозитории: ITenantContext обязан
/// быть установлен до вызова (это делает pipeline после проверки JWT).
/// </para>
/// </summary>
public sealed class SessionBinder(
    ITenantScopedRepository<AgentSession> sessions,
    ITenantScopedRepository<Channel> channels,
    ITenantScopedRepository<Agent> agents)
{
    private const string ClosedStatus = "closed";
    private const string ActiveStatus = "active";

    private readonly ITenantScopedRepository<AgentSession> _sessions =
        sessions ?? throw new ArgumentNullException(nameof(sessions));

    private readonly ITenantScopedRepository<Channel> _channels =
        channels ?? throw new ArgumentNullException(nameof(channels));

    private readonly ITenantScopedRepository<Agent> _agents =
        agents ?? throw new ArgumentNullException(nameof(agents));

    /// <summary>
    /// Возвращает сессию для сообщения визитера: указанную явно (session_id),
    /// последнюю открытую сессию визитера или новую.
    /// </summary>
    /// <param name="tenantId">Тенант, подтверждённый JWT виджета.</param>
    /// <param name="userRef">Идентификатор визитера (cookie/localStorage).</param>
    /// <param name="sessionId">Продолжение конкретной сессии (null — найти/создать).</param>
    /// <param name="channelId">Канал (null — активный виджет-канал тенанта).</param>
    /// <param name="cancellationToken">Отмена.</param>
    public async Task<AgentSession> ResolveAsync(
        Guid tenantId,
        string userRef,
        Guid? sessionId,
        Guid? channelId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("tenant_id is required", nameof(tenantId));
        }

        if (string.IsNullOrWhiteSpace(userRef))
        {
            throw new ArgumentException("user_ref is required", nameof(userRef));
        }

        if (sessionId is { } requestedSessionId)
        {
            var existing = await FindAsync(requestedSessionId, cancellationToken).ConfigureAwait(false);

            if (existing is null)
            {
                throw GatewayException.NotFound(
                    "session_not_found",
                    $"Session {requestedSessionId} is not found for this tenant.");
            }

            if (!string.Equals(existing.UserRef, userRef, StringComparison.Ordinal))
            {
                throw GatewayException.Forbidden(
                    "session_visitor_mismatch",
                    "The session belongs to another visitor.");
            }

            if (string.Equals(existing.Status, ClosedStatus, StringComparison.Ordinal))
            {
                throw GatewayException.Conflict("session_closed", "The session is closed; start a new dialog.");
            }

            return existing;
        }

        var open = await _sessions.Query
            .Where(session => session.UserRef == userRef && session.Status != ClosedStatus)
            .OrderByDescending(session => session.LastActivityAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (open is not null)
        {
            return open;
        }

        var channel = await ResolveChannelAsync(channelId, cancellationToken).ConfigureAwait(false);
        var agent = await ResolveAgentAsync(channel, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        // tenant_id проставляет TenantScopedRepository из ITenantContext.
        var created = new AgentSession
        {
            AgentId = agent.Id,
            AgentVersion = agent.CurrentVersion,
            ChannelId = channel.Id,
            UserRef = userRef,
            Status = SessionStatus.Active.ToDbValue(),
            Meta = "{}",
            StartedAt = now,
            LastActivityAt = now
        };

        await _sessions.AddAsync(created, cancellationToken).ConfigureAwait(false);
        await _sessions.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return created;
    }

    /// <summary>Сессия тенанта по идентификатору или null (сессия другого тенанта не видна).</summary>
    public Task<AgentSession?> FindAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => _sessions.FindAsync(sessionId, cancellationToken);

    /// <summary>Обновляет статус и активность сессии (active | awaiting_gate | escalated | degraded).</summary>
    public async Task SetStatusAsync(
        AgentSession session,
        SessionStatus status,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        session.Status = status.ToDbValue();
        session.LastActivityAt = DateTimeOffset.UtcNow;

        if (status == SessionStatus.Closed)
        {
            session.ClosedAt ??= session.LastActivityAt;
        }

        _sessions.Update(session);
        await _sessions.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Channel> ResolveChannelAsync(Guid? channelId, CancellationToken cancellationToken)
    {
        var widgetType = ChannelType.Widget.ToDbValue();

        var query = _channels.Query.Where(channel =>
            channel.Type == widgetType && channel.Status == ActiveStatus);

        if (channelId is { } requestedChannelId)
        {
            query = query.Where(channel => channel.Id == requestedChannelId);
        }

        return await query
            .OrderBy(channel => channel.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw GatewayException.Conflict(
                "channel_not_configured",
                channelId is null
                    ? "The tenant has no active widget channel."
                    : $"The widget channel {channelId} is not found or disabled.");
    }

    private async Task<Agent> ResolveAgentAsync(Channel channel, CancellationToken cancellationToken)
    {
        var configuredAgentId = ReadAgentId(channel.Config);

        if (configuredAgentId is { } agentId)
        {
            return await _agents.FindAsync(agentId, cancellationToken).ConfigureAwait(false)
                ?? throw GatewayException.Conflict(
                    "agent_not_configured",
                    $"Agent {agentId} from the widget channel config is not found in this tenant.");
        }

        return await _agents.Query
            .OrderBy(agent => agent.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw GatewayException.Conflict(
                "agent_not_configured",
                "The tenant has no agent to serve the widget channel.");
    }

    /// <summary>
    /// Агент канала из channels.config: <c>{"agent_id": "..."}</c>. Невалидная
    /// настройка не ломает приём сообщений — используется агент тенанта по умолчанию.
    /// </summary>
    private static Guid? ReadAgentId(string? config)
    {
        if (string.IsNullOrWhiteSpace(config))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(config);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("agent_id", out var value)
                && value.ValueKind == JsonValueKind.String
                && Guid.TryParse(value.GetString(), out var agentId))
            {
                return agentId;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}
