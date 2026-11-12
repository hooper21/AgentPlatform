using AgentPlatform.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AgentPlatform.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Заполняет created_at / updated_at при сохранении (UTC).
/// По требованию задачи метки ставятся EF-интерсептором, а не триггерами БД.
/// </summary>
public sealed class AuditingInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ApplyTimestamps(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ApplyTimestamps(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void ApplyTimestamps(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            if (entry.State == EntityState.Added
                && entry.Entity is IHasCreatedAt created
                && created.CreatedAt == default)
            {
                created.CreatedAt = now;
            }

            if (entry.Entity is IHasUpdatedAt updated)
            {
                updated.UpdatedAt = now;
            }
        }
    }
}
