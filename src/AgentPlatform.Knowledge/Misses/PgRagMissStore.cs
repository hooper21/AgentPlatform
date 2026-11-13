using AgentPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentPlatform.Knowledge.Misses;

/// <summary>
/// pgvector-журнал rag_misses. Таблица создаётся миграцией KnowledgeRagFoundation
/// (id bigserial, tenant_id uuid, query text, ts timestamptz).
/// </summary>
public sealed class PgRagMissStore(
    AgentPlatformDbContext context,
    ILogger<PgRagMissStore> logger) : IRagMissStore
{
    private readonly AgentPlatformDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    private readonly ILogger<PgRagMissStore> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task LogAsync(
        Guid tenantId,
        string query,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty || string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        try
        {
            await _context.Database
                .ExecuteSqlRawAsync(
                    "INSERT INTO rag_misses (tenant_id, query, ts) VALUES ({0}, {1}, {2});",
                    [tenantId, query, timestamp],
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Метрика не должна влиять на выдачу: поиск уже вернул пустой результат.
            _logger.LogWarning(
                exception,
                "Failed to write rag_misses row for tenant {TenantId}",
                tenantId);
        }
    }
}
