using AgentPlatform.ConnectorHost.Tools;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Enums;

namespace AgentPlatform.Tests.Runtime;

/// <summary>
/// Конфигурация тестового агента с тулами EchoTools (манифест — <see cref="EchoToolsManifest"/>).
/// Read-тул настроен с режимом гейта never (тенант снял подтверждение для read),
/// write-тул — always (подтверждение требуется: политика манифеста write).
/// </summary>
internal static class EchoAgentConfig
{
    public static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static readonly Guid AgentId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public static readonly Guid InstallationId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public const int Version = 1;

    public const string SystemPrompt = "Ты тестовый агент платформы.";

    /// <summary>Создаёт конфигурацию версии агента с тулами echo_read/echo_write.</summary>
    public static AgentRuntimeConfig Create(
        int version = Version,
        int maxTurns = 10,
        int maxToolCalls = 8,
        ToolPolicy writePolicy = ToolPolicy.Write,
        GateMode readGateMode = GateMode.Never,
        GateMode writeGateMode = GateMode.Always,
        int failedTurnsThreshold = 3)
    {
        var manifest = EchoToolsManifest.Create();
        var readTool = manifest.Tools.Single(tool => tool.Name == EchoToolsManifest.ReadTool);
        var writeTool = manifest.Tools.Single(tool => tool.Name == EchoToolsManifest.WriteTool);

        return new AgentRuntimeConfig
        {
            TenantId = TenantId,
            AgentId = AgentId,
            Version = version,
            SystemPrompt = SystemPrompt,
            Model = new AgentModelRef("test-model"),
            Tools =
            [
                CreateTool(readTool.Name, readTool.Description, readTool.InputSchema, readTool.Policy, readGateMode),
                CreateTool(writeTool.Name, writeTool.Description, writeTool.InputSchema, writePolicy, writeGateMode)
            ],
            Limits = new AgentLimits(maxTurns, maxToolCalls),
            EscalationRules = new AgentEscalationRules(failedTurnsThreshold)
        };
    }

    /// <summary>Конфигурация без указанного тула (например, эхо-write отключён в новой версии).</summary>
    public static AgentRuntimeConfig WithoutTool(string toolName, int version = Version)
    {
        var config = Create(version);
        return config with
        {
            Tools = config.Tools.Where(tool => tool.ToolName != toolName).ToList()
        };
    }

    public static AgentToolConfig CreateTool(
        string toolName,
        string description,
        string inputSchema,
        ToolPolicy manifestPolicy,
        GateMode gateMode)
        => new(
            description,
            inputSchema,
            new ToolContext(InstallationId, toolName, manifestPolicy, null, null, gateMode));
}
