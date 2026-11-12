using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;

namespace AgentPlatform.Core.Abstractions;

/// <summary>
/// Хранилище заявок HITL (таблица approvals). Заявка создаётся gate-узлом до паузы
/// (статус <see cref="ApprovalStatus.Pending"/>), поэтому ожидающая сессия не удерживает
/// поток/воркер: resume позже читает заявку из БД, в том числе после рестарта сервиса.
/// Реализация — Infrastructure (EF Core / PostgreSQL).
/// </summary>
public interface IApprovalStore
{
    /// <summary>
    /// Создаёт заявку. Payload валидируется; session_id в payload обязан совпадать
    /// с <see cref="ApprovalRequest.SessionId"/>.
    /// </summary>
    Task<ApprovalRequest> CreateAsync(ApprovalRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Заявка тенанта по идентификатору или null (заявка другого тенанта не видна).
    /// </summary>
    Task<ApprovalRequest?> FindAsync(
        Guid tenantId,
        Guid approvalId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ожидающая заявка сессии (последняя по времени) или null.
    /// Нужна кабинету/оператору: две сессии тенанта имеют независимые гейты.
    /// </summary>
    Task<ApprovalRequest?> FindPendingAsync(
        Guid tenantId,
        Guid sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Фиксирует терминальное решение по pending-заявке (approved | rejected | expired |
    /// superseded). Повторное решение по уже закрытой заявке — <see cref="InvalidOperationException"/>.
    /// </summary>
    Task<ApprovalRequest> DecideAsync(
        Guid tenantId,
        Guid approvalId,
        Decision decision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Отменяет ожидающую заявку сессии (отмена/закрытие сессии): статус superseded,
    /// решение system. Возвращает null, если ожидающих заявок нет.
    /// </summary>
    Task<ApprovalRequest?> CancelPendingAsync(
        Guid tenantId,
        Guid sessionId,
        string? comment = null,
        CancellationToken cancellationToken = default);
}
