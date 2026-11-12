using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Contracts;

namespace AgentPlatform.Scheduler.Connectors;

/// <summary>
/// Вызов тулов коннекторов из Scheduler. Транспорт MCP (streamable HTTP) в v1 ещё
/// не подключён, поэтому Scheduler честно возвращает ошибку: слои платформы не
/// вызывают внешние API напрямую.
/// <para>
/// TODO(2.2) contract: MCP-клиент к Connector Host (лимиты, ретраи, кеш read-тулов).
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
