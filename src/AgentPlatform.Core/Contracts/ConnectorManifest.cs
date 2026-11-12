using AgentPlatform.Core.Enums;

namespace AgentPlatform.Core.Contracts;

/// <summary>
/// Манифест коннектора (connector_registry.manifest): коннектор описывает себя как
/// MCP-сервер — тулы с JSON-схемами и политиками, требования к auth, декларацию
/// лимитов внешнего API и классы кешируемых данных с TTL.
/// <para>
/// JSON-представление манифеста — snake_case (конвенция jsonb-документов платформы),
/// схема-артефакт: <see cref="ConnectorManifestSchema"/>. Проверяется при установке
/// (<see cref="Validation.ConnectorManifestValidator"/>).
/// </para>
/// </summary>
/// <param name="Name">Имя коннектора (уникально вместе с версией).</param>
/// <param name="Version">Версия коннектора в формате semver.</param>
/// <param name="Description">Назначение коннектора для каталога и оператора.</param>
/// <param name="Auth">Требования к аутентификации.</param>
/// <param name="Tools">Тулы, предоставляемые коннектором (минимум один).</param>
/// <param name="Limits">Лимиты внешнего API, которые обязуется соблюдать платформа.</param>
/// <param name="CacheClasses">Классы кеша read-тулов: имя класса → TTL в секундах.</param>
public sealed record ConnectorManifest(
    string Name,
    string Version,
    string Description,
    ConnectorAuth Auth,
    IReadOnlyList<ConnectorToolManifest> Tools,
    ConnectorLimits Limits,
    IReadOnlyDictionary<string, int>? CacheClasses = null);

/// <summary>
/// Требования коннектора к аутентификации: apiKey | longLivedToken | oauth2
/// (external_mcp — платформенные MCP-серверы, не требующие секрета тенанта).
/// Секрет в манифесте не передаётся: только имена ожидаемых полей.
/// </summary>
/// <param name="Type">Тип аутентификации.</param>
/// <param name="Fields">Ожидаемые поля секрета (имена, без значений).</param>
public sealed record ConnectorAuth(ConnectorAuthType Type, IReadOnlyList<string>? Fields = null);

/// <summary>Описание тула в манифесте коннектора.</summary>
/// <param name="Name">Имя тула.</param>
/// <param name="Description">Описание для модели (попадает в промпт как данные).</param>
/// <param name="InputSchema">JSON Schema аргументов тула.</param>
/// <param name="Policy">Политика из манифеста: read | write | destructive.</param>
/// <param name="CacheClass">Класс кеша — только для read-тулов, должен быть объявлен в <see cref="ConnectorManifest.CacheClasses"/>.</param>
/// <param name="CostPerCall">Стоимость вызова у внешнего API (для учёта usage), если известна.</param>
public sealed record ConnectorToolManifest(
    string Name,
    string Description,
    string InputSchema,
    ToolPolicy Policy,
    string? CacheClass = null,
    decimal? CostPerCall = null);

/// <summary>
/// Декларация лимитов коннектора. Платформа соблюдает только декларированные лимиты:
/// очередь на пару tenant × connector, окно запросов, взвешенные квоты по эндпоинтам.
/// </summary>
/// <param name="RequestsPerWindow">Разрешённое число запросов в окне.</param>
/// <param name="WindowSeconds">Длина окна в секундах.</param>
/// <param name="WeightByEndpoint">Вес запроса по эндпоинту (взвешенные квоты): endpoint → weight.</param>
public sealed record ConnectorLimits(
    int RequestsPerWindow,
    int WindowSeconds,
    IReadOnlyDictionary<string, double>? WeightByEndpoint = null);

/// <summary>Ссылка на установленный тул в версии агента (agent_versions.enabled_tools).</summary>
/// <param name="InstallationId">Идентификатор connector_installations.id.</param>
/// <param name="ToolName">Имя тула из манифеста.</param>
/// <param name="Policy">Действующая политика тула.</param>
public sealed record EnabledToolRef(Guid InstallationId, string ToolName, ToolPolicy Policy);
