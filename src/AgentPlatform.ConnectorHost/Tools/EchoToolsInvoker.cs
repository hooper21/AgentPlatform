using System.Text.Json;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Contracts;

namespace AgentPlatform.ConnectorHost.Tools;

/// <summary>
/// Вызов тулов EchoTools внутри процесса — тестовый путь для проверки графа
/// (без сетевого MCP-транспорта). Продакшн-вызов тулов идёт через MCP-клиент
/// к Connector Host: TODO(2.2) contract — транспорт streamable HTTP, лимиты и кеш.
/// </summary>
public sealed class EchoToolsInvoker : IToolInvoker
{
    /// <inheritdoc />
    public Task<ToolInvocationResult> InvokeAsync(
        ToolInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var message = ReadMessage(request.ArgumentsJson);

        var result = request.ToolName switch
        {
            EchoToolsManifest.ReadTool => new ToolInvocationResult(true, EchoTools.echo_read(message)),
            EchoToolsManifest.WriteTool => new ToolInvocationResult(true, EchoTools.echo_write(message)),
            _ => new ToolInvocationResult(
                false,
                string.Empty,
                $"tool '{request.ToolName}' is not hosted by the echo connector")
        };

        return Task.FromResult(result);
    }

    private static string ReadMessage(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("message", out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }

            return root.ToString();
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}
