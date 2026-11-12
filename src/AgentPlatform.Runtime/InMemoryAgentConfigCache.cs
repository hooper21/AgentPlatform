using System.Collections.Concurrent;
using AgentPlatform.Core.Contracts;

namespace AgentPlatform.Runtime;

/// <summary>
/// Кеш конфигурации агента в памяти процесса: используется в dev/тестах и когда
/// Redis не настроен. Часы инжектируются, чтобы TTL (60 с) проверялся в тестах
/// без ожидания.
/// </summary>
public sealed class InMemoryAgentConfigCache : IAgentConfigCache
{
    private readonly ConcurrentDictionary<(Guid TenantId, Guid AgentId, int Version), Entry> _entries = new();
    private readonly TimeSpan _ttl;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    /// <summary>Создаёт кеш с указанным TTL и (опционально) источником времени.</summary>
    public InMemoryAgentConfigCache(TimeSpan ttl, Func<DateTimeOffset>? clock = null)
    {
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be positive.");
        }

        _ttl = ttl;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public Task<AgentRuntimeConfig?> GetAsync(
        Guid tenantId,
        Guid agentId,
        int agentVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var key = (tenantId, agentId, agentVersion);
        if (!_entries.TryGetValue(key, out var entry))
        {
            return Task.FromResult<AgentRuntimeConfig?>(null);
        }

        if (entry.ExpiresAt <= _clock())
        {
            _entries.TryRemove(key, out _);
            return Task.FromResult<AgentRuntimeConfig?>(null);
        }

        return Task.FromResult<AgentRuntimeConfig?>(entry.Config);
    }

    /// <inheritdoc />
    public Task SetAsync(AgentRuntimeConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        cancellationToken.ThrowIfCancellationRequested();

        var key = (config.TenantId, config.AgentId, config.Version);
        _entries[key] = new Entry(config, _clock() + _ttl);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task InvalidateAsync(Guid tenantId, Guid agentId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            foreach (var key in _entries.Keys)
            {
                if (key.TenantId == tenantId && key.AgentId == agentId)
                {
                    _entries.TryRemove(key, out _);
                }
            }
        }

        return Task.CompletedTask;
    }

    private sealed record Entry(AgentRuntimeConfig Config, DateTimeOffset ExpiresAt);
}
