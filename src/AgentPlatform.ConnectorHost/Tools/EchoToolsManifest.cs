using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Enums;

namespace AgentPlatform.ConnectorHost.Tools;

/// <summary>
/// Манифест тестового коннектора EchoTools: read-тул <c>echo_read</c> и write-тул
/// <c>echo_write</c> (всегда проходит через HITL-гейт). Демонстрирует контракт
/// <see cref="ConnectorManifest"/> (schema, policy, cacheClass, limits) и
/// используется тестами графа как эталон конфигурации тулов.
/// </summary>
public static class EchoToolsManifest
{
    /// <summary>Имя коннектора в реестре.</summary>
    public const string ConnectorName = "echo";

    /// <summary>Имя read-тула.</summary>
    public const string ReadTool = "echo_read";

    /// <summary>Имя write-тула.</summary>
    public const string WriteTool = "echo_write";

    /// <summary>Класс кеша read-тула (TTL 60 с).</summary>
    public const string ReadCacheClass = "echo-read";

    /// <summary>Версия коннектора.</summary>
    public const string Version = "1.0.0";

    /// <summary>Создаёт манифест коннектора (проходит <c>ConnectorManifestValidator</c>).</summary>
    public static ConnectorManifest Create() => new(
        Name: ConnectorName,
        Version: Version,
        Description: "Внутренний тестовый коннектор платформы: echo_read (read) и echo_write (write через HITL).",
        Auth: new ConnectorAuth(ConnectorAuthType.ExternalMcp),
        Tools:
        [
            new ConnectorToolManifest(
                Name: ReadTool,
                Description: "Echoes the message back. Read-only test tool.",
                InputSchema: """{"type":"object","properties":{"message":{"type":"string"}},"required":["message"]}""",
                Policy: ToolPolicy.Read,
                CacheClass: ReadCacheClass,
                CostPerCall: 0m),
            new ConnectorToolManifest(
                Name: WriteTool,
                Description: "Echoes the message back. Write tool, always gated by HITL.",
                InputSchema: """{"type":"object","properties":{"message":{"type":"string"}},"required":["message"]}""",
                Policy: ToolPolicy.Write,
                CostPerCall: 0m)
        ],
        Limits: new ConnectorLimits(
            RequestsPerWindow: 60,
            WindowSeconds: 60,
            WeightByEndpoint: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [ReadTool] = 1,
                [WriteTool] = 5
            }),
        CacheClasses: new Dictionary<string, int>(StringComparer.Ordinal) { [ReadCacheClass] = 60 });
}
