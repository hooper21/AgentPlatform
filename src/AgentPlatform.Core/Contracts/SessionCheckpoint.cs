namespace AgentPlatform.Core.Contracts;

/// <summary>
/// Чекпоинт сессии (таблица checkpoints): <see cref="State"/> — нейтральный
/// JSON-контракт (<see cref="StateContract"/>), <see cref="GraphSignatureHash"/> —
/// подпись топологии графа для валидации resume.
/// <para>
/// Нативные структуры оркестратора (MAF) сюда не попадают: смена оркестратора
/// не мигрирует историю сессий.
/// </para>
/// </summary>
/// <param name="TenantId">Тенант сессии.</param>
/// <param name="SessionId">Сессия.</param>
/// <param name="Superstep">Номер суперстепа графа.</param>
/// <param name="State">Нейтральный JSON состояния (messages, pending_gate, counters, version).</param>
/// <param name="GraphSignatureHash">Подпись топологии графа.</param>
/// <param name="CreatedAt">Момент записи чекпоинта (UTC).</param>
public sealed record SessionCheckpoint(
    Guid TenantId,
    Guid SessionId,
    int Superstep,
    string State,
    string GraphSignatureHash,
    DateTimeOffset CreatedAt);
