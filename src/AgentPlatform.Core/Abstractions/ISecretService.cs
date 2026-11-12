namespace AgentPlatform.Core.Abstractions;

/// <summary>
/// Secret Service: шифрованное хранение секретов тенанта (credentials коннекторов).
/// API и логи работают только со ссылкой (<c>connector_installations.credentials_ref</c>),
/// значения секретов наружу не возвращаются.
/// <para>
/// Реализация v1 — Infrastructure (AES-256-GCM + KMS-заглушка). Замена KMS не меняет
/// контракт: ссылки на секреты остаются прежними.
/// </para>
/// </summary>
public interface ISecretService
{
    /// <summary>
    /// Шифрует и сохраняет секрет тенанта. Возвращает ссылку для credentials_ref.
    /// </summary>
    /// <param name="tenantId">Тенант-владелец секрета.</param>
    /// <param name="credentials">Поля секрета (имена из манифеста коннектора).</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<string> StoreAsync(
        Guid tenantId,
        IReadOnlyDictionary<string, string> credentials,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Читает и расшифровывает секрет по ссылке. null — ссылка не найдена у тенанта.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>?> GetAsync(
        Guid tenantId,
        string credentialsRef,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Удаляет секрет по ссылке. false — ссылка не найдена у тенанта.
    /// </summary>
    Task<bool> DeleteAsync(
        Guid tenantId,
        string credentialsRef,
        CancellationToken cancellationToken = default);
}
