using AgentPlatform.Core.Dto;

namespace AgentPlatform.Core.Abstractions;

/// <summary>
/// Порт RAG-контекста для Runtime: по тексту сообщения возвращает релевантные
/// чанки знаний тенанта. Реализация — адаптер Knowledge Service (HTTP) или NoOp.
/// <para>
/// Ошибка/недоступность Knowledge Service не должна ломать диалог: Runtime
/// трактует исключение как пустой контекст (fail-open, см. TurnStartExecutor).
/// </para>
/// </summary>
public interface IKnowledgeContextProvider
{
    /// <summary>
    /// Ищет справочную информацию тенанта по запросу.
    /// Возвращает <see cref="KnowledgeContext.Empty"/> при отсутствии релевантных чанков.
    /// </summary>
    Task<KnowledgeContext> GetContextAsync(
        Guid tenantId,
        string query,
        CancellationToken cancellationToken = default);
}

/// <summary>RAG-порт без базы знаний: всегда пустой контекст (Knowledge Service не подключён).</summary>
public sealed class NullKnowledgeContextProvider : IKnowledgeContextProvider
{
    /// <inheritdoc />
    public Task<KnowledgeContext> GetContextAsync(
        Guid tenantId,
        string query,
        CancellationToken cancellationToken = default)
        => Task.FromResult(KnowledgeContext.Empty(query ?? string.Empty));
}
