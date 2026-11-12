namespace AgentPlatform.Core.Contracts;

/// <summary>
/// Запрос вызова тула коннектора. Runtime не вызывает внешние API напрямую:
/// запрос уходит в Connector Host (MCP-сервер), который знает транспорт и секреты.
/// </summary>
/// <param name="TenantId">Тенант (для выбора установки и секрета).</param>
/// <param name="SessionId">Сессия (для аудита и трейсинга).</param>
/// <param name="InstallationId">Установка коннектора (connector_installations.id).</param>
/// <param name="ToolName">Имя тула из манифеста.</param>
/// <param name="ArgumentsJson">JSON-аргументы вызова (уже замаскированные для хранения).</param>
public sealed record ToolInvocationRequest(
    Guid TenantId,
    Guid SessionId,
    Guid InstallationId,
    string ToolName,
    string ArgumentsJson);

/// <summary>Результат вызова тула коннектора.</summary>
/// <param name="Succeeded">true — тул вернул результат без ошибки.</param>
/// <param name="Content">Текст результата для модели.</param>
/// <param name="Error">Сообщение об ошибке (null при успехе).</param>
/// <param name="ResultRef">Ссылка на полный результат в хранилище (если сохранён).</param>
public sealed record ToolInvocationResult(
    bool Succeeded,
    string Content,
    string? Error = null,
    string? ResultRef = null);
