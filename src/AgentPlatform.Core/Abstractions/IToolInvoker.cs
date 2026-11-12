using AgentPlatform.Core.Contracts;

namespace AgentPlatform.Core.Abstractions;

/// <summary>
/// Вызов тула коннектора через Connector Host (MCP). Слои платформы не вызывают
/// внешние API напрямую: Runtime формирует запрос, транспорт и секреты — на стороне
/// Connector Host.
/// </summary>
public interface IToolInvoker
{
    /// <summary>Вызывает тул установки коннектора и возвращает результат для модели.</summary>
    Task<ToolInvocationResult> InvokeAsync(
        ToolInvocationRequest request,
        CancellationToken cancellationToken = default);
}
