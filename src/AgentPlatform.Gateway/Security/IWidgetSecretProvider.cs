namespace AgentPlatform.Gateway.Security;

/// <summary>
/// Источник секрета HS256 для JWT виджета тенанта.
/// <para>
/// По README секреты тенантов хранятся в Secret Service (AES-256), а channels
/// ссылается на них через widget_secret_ref. Secret Service в v1 ещё не реализован,
/// поэтому контракт выделен отдельно: замена источника не меняет проверку токена.
/// </para>
/// </summary>
public interface IWidgetSecretProvider
{
    /// <summary>
    /// Секрет подписи JWT виджета тенанта или null, если виджет-канал не настроен.
    /// </summary>
    string? GetSecret(Guid tenantId);
}
