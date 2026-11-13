namespace AgentPlatform.Knowledge.Misses;

/// <summary>In-memory журнал rag_misses для тестов и локального запуска.</summary>
public sealed class InMemoryRagMissStore : IRagMissStore
{
    private readonly List<(Guid TenantId, string Query, DateTimeOffset Timestamp)> _entries = [];
    private readonly object _gate = new();

    /// <summary>Записанные промахи (по порядку добавления).</summary>
    public IReadOnlyList<(Guid TenantId, string Query, DateTimeOffset Timestamp)> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }

    /// <inheritdoc />
    public Task LogAsync(
        Guid tenantId,
        string query,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _entries.Add((tenantId, query, timestamp));
        }

        return Task.CompletedTask;
    }
}
