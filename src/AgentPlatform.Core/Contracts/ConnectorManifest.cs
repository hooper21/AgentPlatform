using AgentPlatform.Core.Enums;

namespace AgentPlatform.Core.Contracts;

/// <summary>
/// Манифест коннектора (connector_registry.manifest). Коннектор описывает себя:
/// name, version, auth, tools[] (схема аргументов, политика, cacheClass), limits, cacheClasses.
/// Платформа уважает декларированные лимиты.
/// </summary>
/// <param name="Name">Имя коннектора (уникально вместе с версией).</param>
/// <param name="Version">Версия коннектора.</param>
/// <param name="Publisher">Издатель коннектора.</param>
/// <param name="Auth">Требования к аутентификации.</param>
/// <param name="Tools">Тулы, предоставляемые коннектором.</param>
/// <param name="Limits">Лимиты, которые обязуется соблюдать платформа.</param>
/// <param name="CacheClasses">Классы кеша (read-тулы) и их TTL.</param>
public sealed record ConnectorManifest(
    string Name,
    string Version,
    string Publisher,
    ConnectorAuthRequirement Auth,
    IReadOnlyList<ConnectorToolManifest> Tools,
    ConnectorLimits? Limits = null,
    IReadOnlyDictionary<string, string>? CacheClasses = null);

/// <summary>Требования коннектора к аутентификации. Секрет хранится только по ссылке.</summary>
/// <param name="Type">apiKey | longLivedToken | oauth2 | external_mcp.</param>
/// <param name="Fields">Ожидаемые поля секрета (имена, без значений).</param>
public sealed record ConnectorAuthRequirement(ConnectorAuthType Type, IReadOnlyList<string>? Fields = null);

/// <summary>
/// Описание тула в манифесте коннектора.
/// </summary>
/// <param name="Name">Имя тула.</param>
/// <param name="Description">Описание для модели.</param>
/// <param name="Policy">Политика из манифеста: read | write | destructive.</param>
/// <param name="ArgsSchema">JSON Schema аргументов тула.</param>
/// <param name="CacheClass">Класс кеша (только для read-тулов).</param>
public sealed record ConnectorToolManifest(
    string Name,
    string Description,
    ToolPolicy Policy,
    string? ArgsSchema = null,
    string? CacheClass = null);

/// <summary>Лимиты коннектора, декларируемые в манифесте.</summary>
/// <param name="RequestsPerMinute">Запросов в минуту на пару tenant × connector.</param>
/// <param name="MaxConcurrency">Максимальная параллельность вызовов.</param>
/// <param name="RetryAttempts">Количество ретраев при 429/5xx.</param>
public sealed record ConnectorLimits(
    int? RequestsPerMinute = null,
    int? MaxConcurrency = null,
    int? RetryAttempts = null);

/// <summary>Ссылка на установленный тул в версии агента (agent_versions.enabled_tools).</summary>
/// <param name="InstallationId">Идентификатор connector_installations.id.</param>
/// <param name="ToolName">Имя тула из манифеста.</param>
/// <param name="Policy">Действующая политика тула.</param>
public sealed record EnabledToolRef(Guid InstallationId, string ToolName, ToolPolicy Policy);
