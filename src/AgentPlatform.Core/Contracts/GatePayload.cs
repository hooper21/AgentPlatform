using System.Text.Json;
using System.Text.Json.Nodes;
using AgentPlatform.Core.Json;

namespace AgentPlatform.Core.Contracts;

/// <summary>
/// Полезная нагрузка HITL-гейта. Хранится в approvals.gate_payload (jsonb):
/// <c>{tool, args_masked, human_description, session_id, created_at, undo_hint}</c>.
/// Аргументы в payload всегда маскированные: полные аргументы для исполнения
/// хранятся только в чекпоинте (<see cref="PendingGateState.ArgumentsJson"/>).
/// Валидация обязательна (см. <see cref="Validation.GatePayloadValidator"/>).
/// </summary>
/// <param name="Tool">Имя тула, требующего подтверждения.</param>
/// <param name="ArgsMasked">Аргументы вызова с замаскированными ПДн.</param>
/// <param name="HumanDescription">Человекочитаемое описание действия для подтверждающего.</param>
/// <param name="SessionId">Сессия, в которой возник гейт.</param>
/// <param name="CreatedAt">Момент создания заявки (UTC).</param>
/// <param name="UndoHint">Подсказка, как отменить действие.</param>
public sealed record GatePayload(
    string Tool,
    JsonNode? ArgsMasked,
    string HumanDescription,
    Guid SessionId,
    DateTimeOffset CreatedAt,
    string? UndoHint = null)
{
    /// <summary>Сериализует payload в jsonb-документ (snake_case).</summary>
    public string ToJson() => PlatformJson.SerializeDb(this);

    /// <summary>Читает payload из jsonb-документа.</summary>
    public static GatePayload? FromJson(string json)
        => string.IsNullOrWhiteSpace(json) ? null : PlatformJson.DeserializeDb<GatePayload>(json);
}

/// <summary>
/// Решение по HITL-гейту (approvals.decision). Передаётся в
/// <see cref="Abstractions.IOrchestrationRuntime.ResumeAsync"/>.
/// </summary>
/// <param name="Status">pending | approved | rejected | expired | superseded.</param>
/// <param name="DecidedByKind">operator | user | system.</param>
/// <param name="DecidedBy">Пользователь кабинета, принявший решение (null для визитера канала).</param>
/// <param name="Comment">Комментарий к решению.</param>
/// <param name="DecidedAt">Момент решения (UTC).</param>
public sealed record Decision(
    Enums.ApprovalStatus Status,
    Enums.ApprovalDecisionKind DecidedByKind,
    Guid? DecidedBy = null,
    string? Comment = null,
    DateTimeOffset? DecidedAt = null);
