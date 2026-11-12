namespace AgentPlatform.TenantApp.Web;

/// <summary>Агент кабинета (ответ Controller API).</summary>
public sealed record AgentDto(
    Guid Id,
    Guid TenantId,
    string Name,
    int CurrentVersion,
    string SystemPrompt,
    string Model,
    string Routing,
    int MaxTurns,
    int MaxToolCalls,
    IReadOnlyList<AgentToolDto> Tools,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Тул агента: манифестная/действующая политика и режим гейта.</summary>
public sealed record AgentToolDto(
    Guid InstallationId,
    string ToolName,
    string ManifestPolicy,
    string EffectivePolicy,
    string PolicySource,
    string GateMode);

/// <summary>Тул, включаемый в новую версию агента.</summary>
public sealed record AgentToolSaveDto(
    Guid InstallationId,
    string ToolName,
    string? Policy,
    string? GateSettings);

/// <summary>Создание/изменение агента (изменение публикует новую версию).</summary>
public sealed record AgentSaveDto(
    string Name,
    string SystemPrompt,
    string Model,
    string? Routing,
    IReadOnlyList<AgentToolSaveDto> Tools,
    int? MaxTurns,
    int? MaxToolCalls,
    Guid? ActorId);

/// <summary>Тул в каталоге коннектора.</summary>
public sealed record ConnectorToolDto(string Name, string Description, string Policy, string InputSchema);

/// <summary>Запись каталога коннекторов.</summary>
public sealed record ConnectorCatalogDto(
    Guid Id,
    string Name,
    string Version,
    string Description,
    string AuthType,
    IReadOnlyList<string> AuthFields,
    IReadOnlyList<ConnectorToolDto> Tools);

/// <summary>Установленный коннектор.</summary>
public sealed record ConnectorInstallationDto(
    Guid Id,
    Guid TenantId,
    Guid RegistryId,
    string ConnectorName,
    string ConnectorVersion,
    string AuthType,
    string Status,
    string? ExternalUrl,
    int ToolCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Запрос установки коннектора (credentials не возвращаются API).</summary>
public sealed record ConnectorInstallDto(
    Guid RegistryId,
    IReadOnlyDictionary<string, string>? Credentials,
    string? ExternalUrl);

/// <summary>Изменение установки коннектора.</summary>
public sealed record ConnectorInstallUpdateDto(
    string? Status,
    string? ExternalUrl,
    IReadOnlyDictionary<string, string>? Credentials);

/// <summary>Карточка ожидающего HITL-гейта (аргументы замаскированы).</summary>
public sealed record ApprovalCardDto(
    Guid ApprovalId,
    string Tool,
    string HumanDescription,
    string ArgsMasked,
    string Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt,
    string? UndoHint);

/// <summary>Сообщение диалога.</summary>
public sealed record DialogMessageDto(long Id, string Role, string Content, DateTimeOffset Timestamp);

/// <summary>Сессия диалога в списке.</summary>
public sealed record DialogSessionDto(
    Guid Id,
    Guid AgentId,
    string AgentName,
    string Status,
    string UserRef,
    DateTimeOffset StartedAt,
    DateTimeOffset LastActivityAt,
    ApprovalCardDto? PendingApproval);

/// <summary>Сессия диалога с сообщениями и гейтом.</summary>
public sealed record DialogDto(
    Guid SessionId,
    Guid AgentId,
    string AgentName,
    string Status,
    string UserRef,
    DateTimeOffset StartedAt,
    DateTimeOffset LastActivityAt,
    IReadOnlyList<DialogMessageDto> Messages,
    ApprovalCardDto? PendingApproval);

/// <summary>Начало диалога.</summary>
public sealed record DialogStartDto(Guid AgentId, string Message, string? UserRef);

/// <summary>Сообщение в диалоге.</summary>
public sealed record DialogMessageRequestDto(string Message);

/// <summary>Решение по гейту.</summary>
public sealed record ApprovalDecisionDto(string Decision, string? Comment, Guid? ActorId);
