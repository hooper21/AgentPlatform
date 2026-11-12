namespace AgentPlatform.Core.Enums;

/// <summary>
/// Политика тула. Проверяется серверно (Policy Engine), повышение политики конфигурацией запрещено.
/// Значения БД: read | write | destructive.
/// </summary>
public enum ToolPolicy
{
    Read = 0,
    Write = 1,
    Destructive = 2
}

/// <summary>Источник политики тула. Значения БД: manifest | tenant_override.</summary>
public enum PolicySource
{
    Manifest = 0,
    TenantOverride = 1
}

/// <summary>Режим HITL-гейта. Значения БД: always | conditional | never.</summary>
public enum GateMode
{
    Always = 0,
    Conditional = 1,
    Never = 2
}

/// <summary>
/// Статус сессии. Значения БД: active | awaiting_gate | escalated | operator | closed | degraded.
/// </summary>
public enum SessionStatus
{
    Active = 0,
    AwaitingGate = 1,
    Escalated = 2,
    Operator = 3,
    Closed = 4,
    Degraded = 5
}

/// <summary>Роль сообщения в диалоге. Значения БД: user | assistant | tool | operator | system.</summary>
public enum MessageRole
{
    User = 0,
    Assistant = 1,
    Tool = 2,
    Operator = 3,
    System = 4
}

/// <summary>
/// Исход вызова тула. Значения БД: ok | error | denied | degraded | gate_required.
/// </summary>
public enum ToolCallOutcome
{
    Ok = 0,
    Error = 1,
    Denied = 2,
    Degraded = 3,
    GateRequired = 4
}

/// <summary>Статус заявки HITL. Значения БД: pending | approved | rejected | expired | superseded.</summary>
public enum ApprovalStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Expired = 3,
    Superseded = 4
}

/// <summary>Кто принял решение по гейту. Значения БД: operator | user | system.</summary>
public enum ApprovalDecisionKind
{
    Operator = 0,
    User = 1,
    System = 2
}

/// <summary>Статус версии агента. Значения БД: draft | proposed | published | archived.</summary>
public enum AgentVersionStatus
{
    Draft = 0,
    Proposed = 1,
    Published = 2,
    Archived = 3
}

/// <summary>Статус записи каталога коннекторов. Значения БД: draft | published | deprecated.</summary>
public enum ConnectorRegistryStatus
{
    Draft = 0,
    Published = 1,
    Deprecated = 2
}

/// <summary>Статус установки коннектора. Значения БД: active | error | disabled.</summary>
public enum ConnectorInstallationStatus
{
    Active = 0,
    Error = 1,
    Disabled = 2
}

/// <summary>
/// Тип аутентификации коннектора. Значения БД: apiKey | longLivedToken | oauth2 | external_mcp.
/// </summary>
public enum ConnectorAuthType
{
    ApiKey = 0,
    LongLivedToken = 1,
    OAuth2 = 2,
    ExternalMcp = 3
}

/// <summary>Тип канала. Значения БД: widget | telegram (+ типы плагинов).</summary>
public enum ChannelType
{
    Widget = 0,
    Telegram = 1
}

/// <summary>Тип источника знаний. Значения БД: document | site | faq.</summary>
public enum KnowledgeSourceType
{
    Document = 0,
    Site = 1,
    Faq = 2
}

/// <summary>Статус индексации источника. Значения БД: pending | indexing | ready | error.</summary>
public enum KnowledgeIndexStatus
{
    Pending = 0,
    Indexing = 1,
    Ready = 2,
    Error = 3
}

/// <summary>Тарифный план тенанта. Значения БД: pilot | basic | unlimited.</summary>
public enum TenantPlan
{
    Pilot = 0,
    Basic = 1,
    Unlimited = 2
}

/// <summary>Статус тенанта. Значения БД: active | blocked.</summary>
public enum TenantStatus
{
    Active = 0,
    Blocked = 1
}

/// <summary>Роль пользователя кабинета. Значения БД: admin | operator | viewer.</summary>
public enum UserRole
{
    Admin = 0,
    Operator = 1,
    Viewer = 2
}

/// <summary>Режим LLM тенанта. Значения БД: platform | own.</summary>
public enum LlmMode
{
    Platform = 0,
    Own = 1
}

/// <summary>Маршрутизация модели. Значения БД: simple | complex.</summary>
public enum ModelRouting
{
    Simple = 0,
    Complex = 1
}

/// <summary>Статус последнего запуска планировщика. Значения БД: ok | error.</summary>
public enum ScheduledRunStatus
{
    Ok = 0,
    Error = 1
}

/// <summary>Итог шага оркестратора.</summary>
public enum RunOutcome
{
    Completed = 0,
    PausedForGate = 1,
    Escalated = 2,
    Degraded = 3
}
