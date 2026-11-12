namespace AgentPlatform.Core.Dto;

/// <summary>Ответ healthcheck-эндпоинта сервиса платформы.</summary>
/// <param name="Status">ok | degraded.</param>
/// <param name="Service">Имя сервиса.</param>
/// <param name="Utc">Момент проверки (UTC).</param>
/// <param name="Stub">true, если сервис работает в режиме заглушки.</param>
public sealed record ServiceHealth(string Status, string Service, DateTimeOffset Utc, bool Stub = true);
