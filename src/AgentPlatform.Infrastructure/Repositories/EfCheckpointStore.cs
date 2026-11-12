using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Infrastructure.Repositories;

/// <summary>
/// Чекпоинты сессий в PostgreSQL (порт <see cref="ICheckpointStore"/>).
/// <para>
/// В checkpoints.state пишется только нейтральный JSON <see cref="StateContract"/>:
/// состояние валидируется до записи и при чтении, поэтому нативные структуры
/// оркестратора в БД не попадают.
/// </para>
/// </summary>
public sealed class EfCheckpointStore(AgentPlatformDbContext context) : ICheckpointStore
{
    private readonly AgentPlatformDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public async Task<SessionCheckpoint?> GetLatestAsync(
        Guid tenantId,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("tenant_id is required", nameof(tenantId));
        }

        var checkpoint = await _context.Checkpoints
            .AsNoTracking()
            .Where(candidate => candidate.TenantId == tenantId && candidate.SessionId == sessionId)
            .OrderByDescending(candidate => candidate.Superstep)
            .ThenByDescending(candidate => candidate.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return checkpoint is null
            ? null
            : new SessionCheckpoint(
                checkpoint.TenantId,
                checkpoint.SessionId,
                checkpoint.Superstep,
                checkpoint.State,
                checkpoint.GraphSignatureHash,
                checkpoint.CreatedAt);
    }

    /// <inheritdoc />
    public async Task SaveAsync(SessionCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        // Страховка: невалидное (или рантайм-специфичное) состояние в БД не пишется.
        StateContract.DeserializeRequired(checkpoint.State);

        _context.Checkpoints.Add(new Checkpoint
        {
            SessionId = checkpoint.SessionId,
            TenantId = checkpoint.TenantId,
            Superstep = checkpoint.Superstep,
            State = checkpoint.State,
            GraphSignatureHash = checkpoint.GraphSignatureHash,
            CreatedAt = checkpoint.CreatedAt == default ? DateTimeOffset.UtcNow : checkpoint.CreatedAt
        });

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
