using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Contracts;

namespace AgentPlatform.Gateway.Connectors;

/// <summary>
/// Вызов тулов через Connector Host. Транспорт MCP (streamable HTTP) в v1 ещё не
/// подключён: слои платформы не вызывают внешние API напрямую, поэтому Gateway
/// отдаёт честную ошибку вместо «прямого» вызова коннектора.
/// <para>
/// TODO(2.2) contract: MCP-клиент к Connector Host (лимиты, ретраи, кеш read-тулов).
/// Тесты Gateway подменяют эту реализацию на in-process EchoToolsInvoker.
/// </para>
/// </summary>
public sealed class UnavailableConnectorToolInvoker : IToolInvoker
{
    /// <inheritdoc />
    public Task<ToolInvocationResult> InvokeAsync(
        ToolInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new ToolInvocationResult(
            Succeeded: false,
            Content: string.Empty,
            Error: "connector host transport is not implemented yet (TODO 2.2 contract)"));
    }
}
