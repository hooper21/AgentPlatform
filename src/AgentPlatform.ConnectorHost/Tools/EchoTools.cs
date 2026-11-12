using System.ComponentModel;
using ModelContextProtocol.Server;

namespace AgentPlatform.ConnectorHost.Tools;

/// <summary>
/// EchoTools — внутренний тестовый коннектор платформы: один read-тул и один write-тул.
/// Нужен для проверки графа, политик и HITL-гейтов (задачи фазы 2).
/// </summary>
[McpServerToolType]
public sealed class EchoTools
{
    /// <summary>Read-тул: возвращает входную строку.</summary>
    [McpServerTool, Description("Echoes the message back. Read-only test tool.")]
    public static string echo_read(string message) => $"echo: {message}";

    /// <summary>Write-тул: по манифесту требует HITL-подтверждения.</summary>
    [McpServerTool, Description("Echoes the message back and is classified as a write tool, so the platform gates it.")]
    public static string echo_write(string message) => $"echo(write): {message}";
}
