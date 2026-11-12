namespace AgentPlatform.Gateway.Security;

/// <summary>
/// Идентичность, подтверждённая JWT виджета: тенант и (если токен её содержит)
/// ссылка на визитера. tenant_key в токене обязан совпадать с ключом в пути
/// запроса — токен одного тенанта нельзя применить к эндпоинту другого.
/// </summary>
/// <param name="TenantId">Тенант из claim tenant_key.</param>
/// <param name="TenantKey">tenant_key в каноническом виде (uuid).</param>
/// <param name="UserRef">user_ref/sub из токена (null — визитер не закреплён в токене).</param>
public sealed record WidgetIdentity(Guid TenantId, string TenantKey, string? UserRef);
