using AgentPlatform.Core.Contracts;

namespace AgentPlatform.Core.Abstractions;

/// <summary>
/// Хранилище чекпоинтов сессии (таблица checkpoints). Состояние — только
/// нейтральный JSON <see cref="StateContract"/>; нативные чекпоинты оркестратора
/// не сохраняются. Реализация — Infrastructure (EF Core / PostgreSQL).
/// </summary>
public interface ICheckpointStore
{
    /// <summary>
    /// Последний чекпоинт сессии в тенанте или null, если чекпоинтов нет.
    /// </summary>
    Task<SessionCheckpoint?> GetLatestAsync(
        Guid tenantId,
        Guid sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Сохраняет чекпоинт суперстепа. Реализация обязана отклонить состояние,
    /// не проходящее <see cref="StateContract"/>.
    /// </summary>
    Task SaveAsync(SessionCheckpoint checkpoint, CancellationToken cancellationToken = default);
}
