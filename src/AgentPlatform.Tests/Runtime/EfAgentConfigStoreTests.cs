using AgentPlatform.ConnectorHost.Tools;
using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Json;
using AgentPlatform.Core.Policy;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Persistence.Entities;
using AgentPlatform.Infrastructure.Persistence.Interceptors;
using AgentPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AgentPlatform.Tests.Runtime;

/// <summary>
/// EfAgentConfigStore: конфигурация версии агента собирается из agents/agent_versions,
/// установленных коннекторов (манифест) и tool_settings; отключённые коннекторы и
/// тулы не попадают в агента, повышение политики отклоняется.
/// </summary>
public sealed class EfAgentConfigStoreTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid InstallationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid RegistryId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public async Task Config_is_assembled_from_agent_version_connector_and_tool_settings()
    {
        using var database = CreateDatabase();
        SeedAgent(database.Context);

        var config = await new EfAgentConfigStore(database.Context)
            .GetAsync(TenantId, AgentId, agentVersion: 1, CancellationToken.None);

        Assert.NotNull(config);
        Assert.Equal(TenantId, config!.TenantId);
        Assert.Equal(AgentId, config.AgentId);
        Assert.Equal(1, config.Version);
        Assert.Equal("Тестовый промпт", config.SystemPrompt);
        Assert.Equal("test-model", config.Model.Model);
        Assert.Equal(5, config.Limits.MaxTurns);
        Assert.Equal(3, config.Limits.MaxToolCalls);
        Assert.Equal(1, config.EscalationRules.FailedTurnsThreshold);

        var readTool = Assert.Single(config.Tools, tool => tool.ToolName == EchoToolsManifest.ReadTool);
        Assert.Equal(ToolPolicy.Read, readTool.Tool.ManifestPolicy);
        Assert.Equal(GateMode.Never, readTool.Tool.TenantGateMode);

        var writeTool = Assert.Single(config.Tools, tool => tool.ToolName == EchoToolsManifest.WriteTool);
        Assert.Equal(ToolPolicy.Write, writeTool.Tool.ManifestPolicy);
        Assert.Null(writeTool.Tool.TenantGateMode);
        Assert.Equal(InstallationId, writeTool.InstallationId);
    }

    [Fact]
    public async Task Disabled_connector_and_disabled_tool_are_not_exposed()
    {
        using var database = CreateDatabase();
        SeedAgent(
            database.Context,
            installationStatus: "disabled",
            readToolEnabled: false);

        var config = await new EfAgentConfigStore(database.Context)
            .GetAsync(TenantId, AgentId, 1, CancellationToken.None);

        Assert.NotNull(config);
        Assert.Empty(config!.Tools);
    }

    [Fact]
    public async Task Policy_escalation_in_tool_settings_is_rejected()
    {
        using var database = CreateDatabase();
        SeedAgent(database.Context, readToolPolicy: "write", readToolPolicySource: "tenant_override");

        await Assert.ThrowsAsync<PolicyEscalationException>(
            () => new EfAgentConfigStore(database.Context).GetAsync(TenantId, AgentId, 1, CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_version_or_foreign_tenant_returns_null()
    {
        using var database = CreateDatabase();
        SeedAgent(database.Context);

        var store = new EfAgentConfigStore(database.Context);

        Assert.Null(await store.GetAsync(TenantId, AgentId, 99, CancellationToken.None));
        Assert.Null(await store.GetAsync(Guid.NewGuid(), AgentId, 1, CancellationToken.None));
        Assert.Null(await store.GetAsync(TenantId, Guid.NewGuid(), 1, CancellationToken.None));
    }

    private static SeedDatabase CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<AgentPlatformDbContext>()
            .UseInMemoryDatabase($"agentplatform-config-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(new AuditingInterceptor())
            .Options;

        var context = new AgentPlatformDbContext(options);
        context.Database.EnsureCreated();

        return new SeedDatabase(context);
    }

    private static void SeedAgent(
        AgentPlatformDbContext context,
        string installationStatus = "active",
        bool readToolEnabled = true,
        string readToolPolicy = "read",
        string readToolPolicySource = "tenant_override")
    {
        var manifest = EchoToolsManifest.Create();

        context.ConnectorRegistry.Add(new ConnectorRegistryEntry
        {
            Id = RegistryId,
            Name = manifest.Name,
            Version = manifest.Version,
            Manifest = PlatformJson.SerializeDb(manifest),
            ManifestHash = new string('a', 64),
            Publisher = "platform",
            Status = "published",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        context.ConnectorInstallations.Add(new ConnectorInstallation
        {
            Id = InstallationId,
            TenantId = TenantId,
            RegistryId = RegistryId,
            CredentialsRef = "secret://echo",
            AuthType = "external_mcp",
            Status = installationStatus,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        context.ToolSettings.Add(new ToolSetting
        {
            Id = Guid.NewGuid(),
            InstallationId = InstallationId,
            ToolName = EchoToolsManifest.ReadTool,
            Enabled = readToolEnabled,
            Policy = readToolPolicy,
            PolicySource = readToolPolicySource,
            GateSettings = """{"mode":"never"}""",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        context.Agents.Add(new Agent
        {
            Id = AgentId,
            TenantId = TenantId,
            Name = "echo-agent",
            CurrentVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        context.AgentVersions.Add(new AgentVersion
        {
            Id = Guid.NewGuid(),
            AgentId = AgentId,
            Version = 1,
            Status = "published",
            SystemPrompt = "Тестовый промпт",
            ModelRef = """{"model":"test-model","routing":"simple"}""",
            EnabledTools =
                $$"""[{"installation_id":"{{InstallationId}}","tool_name":"{{EchoToolsManifest.ReadTool}}","policy":"read"},{"installation_id":"{{InstallationId}}","tool_name":"{{EchoToolsManifest.WriteTool}}","policy":"write"}]""",
            Rules = "{}",
            EscalationRules = """{"failed_turns":1}""",
            Limits = """{"max_turns":5,"max_tool_calls":3}""",
            CreatedAt = DateTimeOffset.UtcNow
        });

        context.SaveChanges();
    }

    private sealed class SeedDatabase(AgentPlatformDbContext context) : IDisposable
    {
        public AgentPlatformDbContext Context { get; } = context;

        public void Dispose() => Context.Dispose();
    }

}
