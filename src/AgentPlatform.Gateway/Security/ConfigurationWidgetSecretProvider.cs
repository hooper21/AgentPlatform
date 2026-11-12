namespace AgentPlatform.Gateway.Security;

/// <summary>
/// Временный источник секретов виджета: конфигурация приложения
/// (<c>Gateway:Widget:Secrets:&lt;tenant_id&gt;</c>, в окружении —
/// <c>Gateway__Widget__Secrets__&lt;tenant_id&gt;</c>).
/// <para>
/// TODO(secret-service): заменить на Secret Service (AES-256) и читать секрет по
/// channels.widget_secret_ref; в файлах конфигурации секреты не хранятся (только env).
/// </para>
/// </summary>
public sealed class ConfigurationWidgetSecretProvider(IConfiguration configuration) : IWidgetSecretProvider
{
    /// <summary>Секция конфигурации с секретами виджета по тенантам.</summary>
    public const string SectionName = "Gateway:Widget:Secrets";

    private readonly IConfiguration _configuration =
        configuration ?? throw new ArgumentNullException(nameof(configuration));

    /// <inheritdoc />
    public string? GetSecret(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            return null;
        }

        var section = _configuration.GetSection(SectionName);

        // Ключ принимается в обоих форматах uuid: в конфигурации удобнее "D", в env — как получится.
        var secret = section[tenantId.ToString("D")] ?? section[tenantId.ToString("N")];

        return string.IsNullOrWhiteSpace(secret) ? null : secret;
    }
}
