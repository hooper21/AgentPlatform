using AgentPlatform.Core.Abstractions;

namespace AgentPlatform.Knowledge.Ingest;

/// <summary>
/// Доступ плагина знаний к учётным данным источника. Контракт
/// <see cref="IKnowledgeProvider"/> не передаёт tenant_id, поэтому провайдер
/// получает секрет через этот порт: реализация берёт текущего тенанта из
/// <see cref="ITenantContext"/> и читает Secret Service.
/// <para>
/// TODO: contract — если IKnowledgeProvider будет расширен tenant_id или готовым
/// объектом credentials, порт можно удалить.
/// </para>
/// </summary>
public interface IKnowledgeSecretResolver
{
    /// <summary>
    /// Временные учётные данные мастера подключения (TestConnection и выбор
    /// пространств до сохранения секрета). Живут только в scope текущего запроса.
    /// null — использовать сохранённый секрет по credentials_ref.
    /// </summary>
    void UseEphemeral(IReadOnlyDictionary<string, string>? credentials);

    /// <summary>
    /// Возвращает поля секрета: имя поля → значение. null — секрет не задан.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>?> ResolveAsync(
        string? credentialsRef,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Реализация <see cref="IKnowledgeSecretResolver"/> поверх Secret Service.
/// Секреты никогда не попадают в config/index_meta и не возвращаются API.
/// </summary>
public sealed class TenantKnowledgeSecretResolver(
    ISecretService secrets,
    ITenantContext tenantContext) : IKnowledgeSecretResolver
{
    private readonly ISecretService _secrets =
        secrets ?? throw new ArgumentNullException(nameof(secrets));

    private readonly ITenantContext _tenantContext =
        tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));

    private IReadOnlyDictionary<string, string>? _ephemeral;

    /// <inheritdoc />
    public void UseEphemeral(IReadOnlyDictionary<string, string>? credentials)
        => _ephemeral = credentials is { Count: > 0 } ? credentials : null;

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>?> ResolveAsync(
        string? credentialsRef,
        CancellationToken cancellationToken = default)
    {
        if (_ephemeral is { Count: > 0 })
        {
            return _ephemeral;
        }

        if (string.IsNullOrWhiteSpace(credentialsRef))
        {
            return null;
        }

        if (!_tenantContext.IsResolved)
        {
            throw new KnowledgeProviderConfigurationException(
                "Не определён tenant для чтения учётных данных источника.");
        }

        return await _secrets
            .GetAsync(_tenantContext.TenantId!.Value, credentialsRef, cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Ошибка конфигурации подключения провайдера (неверный baseUrl, нет нужного
/// поля секрета и т.п.). Возвращается клиенту как 400, а не 500.
/// </summary>
public sealed class KnowledgeProviderConfigurationException(string message) : Exception(message);
