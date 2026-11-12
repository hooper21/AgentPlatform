using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Validation;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentPlatform.Infrastructure.Repositories;

/// <summary>
/// Заявки HITL-гейтов в PostgreSQL (порт <see cref="IApprovalStore"/>).
/// <para>
/// Заявка создаётся до паузы и хранится в таблице approvals, поэтому ожидающая
/// сессия не удерживает поток: решение может прийти позже, в том числе после
/// рестарта сервиса. Все запросы фильтруются по tenant_id, гейты разных сессий
/// тенанта независимы (поиск по session_id/approval_id).
/// </para>
/// <para>
/// В DDL approvals нет столбца comment: комментарий решения принимается контрактом,
/// но в v1 не сохраняется (расширение схемы — отдельная задача).
/// </para>
/// </summary>
public sealed class EfApprovalStore(AgentPlatformDbContext context) : IApprovalStore
{
    private const string PendingStatus = "pending";

    /// <summary>Таймаут по умолчанию, если в строке approvals не заполнен expires_at.</summary>
    private static readonly TimeSpan FallbackTimeout = TimeSpan.FromHours(24);

    private readonly AgentPlatformDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public async Task<ApprovalRequest> CreateAsync(
        ApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.TenantId == Guid.Empty)
        {
            throw new ArgumentException("tenant_id is required", nameof(request));
        }

        if (request.SessionId == Guid.Empty)
        {
            throw new ArgumentException("session_id is required", nameof(request));
        }

        if (request.Payload.SessionId != request.SessionId)
        {
            throw new InvalidOperationException(
                "approval payload session_id must match the approval session_id.");
        }

        GatePayloadValidator.Validate(request.Payload);

        var entity = new Approval
        {
            Id = request.Id == Guid.Empty ? Guid.NewGuid() : request.Id,
            TenantId = request.TenantId,
            SessionId = request.SessionId,
            GatePayload = request.Payload.ToJson(),
            Status = request.Status.ToDbValue(),
            RequestedAt = request.RequestedAt == default ? DateTimeOffset.UtcNow : request.RequestedAt,
            ExpiresAt = request.ExpiresAt == default
                ? DateTimeOffset.UtcNow + FallbackTimeout
                : request.ExpiresAt,
            DecidedBy = request.DecidedBy,
            DecidedByKind = request.DecidedByKind?.ToDbValue(),
            Decision = request.Status == ApprovalStatus.Pending ? null : request.Status.ToDbValue(),
            DecidedAt = request.DecidedAt
        };

        _context.Approvals.Add(entity);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToContract(entity);
    }

    /// <inheritdoc />
    public async Task<ApprovalRequest?> FindAsync(
        Guid tenantId,
        Guid approvalId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);

        var entity = await _context.Approvals
            .AsNoTracking()
            .FirstOrDefaultAsync(
                approval => approval.TenantId == tenantId && approval.Id == approvalId,
                cancellationToken)
            .ConfigureAwait(false);

        return entity is null ? null : ToContract(entity);
    }

    /// <inheritdoc />
    public async Task<ApprovalRequest?> FindPendingAsync(
        Guid tenantId,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);

        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("session_id is required", nameof(sessionId));
        }

        var entity = await _context.Approvals
            .AsNoTracking()
            .Where(approval => approval.TenantId == tenantId
                && approval.SessionId == sessionId
                && approval.Status == PendingStatus)
            .OrderByDescending(approval => approval.RequestedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return entity is null ? null : ToContract(entity);
    }

    /// <inheritdoc />
    public async Task<ApprovalRequest> DecideAsync(
        Guid tenantId,
        Guid approvalId,
        Decision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        RequireTenant(tenantId);

        if (decision.Status == ApprovalStatus.Pending)
        {
            throw new ArgumentException(
                "decision must be terminal (approved | rejected | expired | superseded)",
                nameof(decision));
        }

        var entity = await _context.Approvals
            .FirstOrDefaultAsync(
                approval => approval.TenantId == tenantId && approval.Id == approvalId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Approval {approvalId} is not found for tenant {tenantId}.");

        if (!EnumTokens.TryParseDbValue<ApprovalStatus>(entity.Status, out var status)
            || status != ApprovalStatus.Pending)
        {
            throw new InvalidOperationException(
                $"Approval {approvalId} is already decided (status '{entity.Status}').");
        }

        entity.Status = decision.Status.ToDbValue();
        entity.Decision = decision.Status.ToDbValue();
        entity.DecidedBy = decision.DecidedBy;
        entity.DecidedByKind = decision.DecidedByKind.ToDbValue();
        entity.DecidedAt = decision.DecidedAt ?? DateTimeOffset.UtcNow;

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToContract(entity);
    }

    /// <inheritdoc />
    public async Task<ApprovalRequest?> CancelPendingAsync(
        Guid tenantId,
        Guid sessionId,
        string? comment = null,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);

        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("session_id is required", nameof(sessionId));
        }

        var entity = await _context.Approvals
            .Where(approval => approval.TenantId == tenantId
                && approval.SessionId == sessionId
                && approval.Status == PendingStatus)
            .OrderByDescending(approval => approval.RequestedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            return null;
        }

        entity.Status = ApprovalStatus.Superseded.ToDbValue();
        entity.Decision = ApprovalStatus.Superseded.ToDbValue();
        entity.DecidedByKind = ApprovalDecisionKind.System.ToDbValue();
        entity.DecidedAt = DateTimeOffset.UtcNow;

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToContract(entity, comment);
    }

    private static void RequireTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("tenant_id is required", nameof(tenantId));
        }
    }

    private static ApprovalRequest ToContract(Approval entity, string? comment = null)
    {
        var payload = GatePayload.FromJson(entity.GatePayload)
            ?? throw new InvalidOperationException(
                $"Approval {entity.Id} has no gate_payload; the approvals contract is violated.");

        var status = EnumTokens.TryParseDbValue<ApprovalStatus>(entity.Status, out var parsedStatus)
            ? parsedStatus
            : throw new InvalidOperationException(
                $"Approval {entity.Id} has unknown status '{entity.Status}'.");

        var decidedByKind = EnumTokens.TryParseDbValue<ApprovalDecisionKind>(
            entity.DecidedByKind,
            out var parsedKind)
            ? parsedKind
            : (ApprovalDecisionKind?)null;

        return new ApprovalRequest(
            entity.Id,
            entity.TenantId,
            entity.SessionId,
            payload,
            status,
            entity.RequestedAt,
            entity.ExpiresAt ?? entity.RequestedAt + FallbackTimeout,
            entity.DecidedBy,
            decidedByKind,
            comment,
            entity.DecidedAt);
    }
}
