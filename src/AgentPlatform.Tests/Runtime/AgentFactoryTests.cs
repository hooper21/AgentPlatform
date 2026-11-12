using AgentPlatform.ConnectorHost.Tools;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;
using AgentPlatform.Runtime;
using AgentPlatform.Runtime.Orchestration;

namespace AgentPlatform.Tests.Runtime;

/// <summary>
/// AgentFactory: агент собирается из конфигурации тенанта (промпт, модель, активные
/// тулы), версия закрепляется за сессией, конфигурация кешируется с TTL 60 с и
/// инвалидируется при изменении.
/// </summary>
public sealed class AgentFactoryTests
{
    [Fact]
    public async Task Agent_is_assembled_with_prompt_model_and_active_tools()
    {
        var config = EchoAgentConfig.Create();
        var store = new FakeAgentConfigStore(config);
        var factory = CreateFactory(store, new InMemoryAgentConfigCache(TimeSpan.FromSeconds(60)));

        var agent = await factory.CreateAsync(CreateSession(config, version: 1), CancellationToken.None);

        Assert.Equal(EchoAgentConfig.SystemPrompt, agent.Config.SystemPrompt);
        Assert.Equal("test-model", agent.Config.Model.Model);
        Assert.Equal(2, agent.Config.Tools.Count);
        Assert.Equal(2, agent.ToolDeclarations.Count);
        Assert.Contains(agent.ToolDeclarations, declaration => declaration.Name == EchoToolsManifest.ReadTool);
        Assert.Contains(agent.ToolDeclarations, declaration => declaration.Name == EchoToolsManifest.WriteTool);
        Assert.NotNull(agent.FindTool(EchoToolsManifest.ReadTool));
    }

    [Fact]
    public async Task Session_uses_pinned_version_even_when_newer_version_exists()
    {
        var version1 = EchoAgentConfig.Create(version: 1);
        var version2 = EchoAgentConfig.WithoutTool(EchoToolsManifest.ReadTool, version: 2);

        var store = new FakeAgentConfigStore(version1, version2);
        var factory = CreateFactory(store, new InMemoryAgentConfigCache(TimeSpan.FromSeconds(60)));

        var pinnedToV1 = await factory.CreateAsync(CreateSession(version1, version: 1), CancellationToken.None);
        Assert.Equal(1, pinnedToV1.Config.Version);
        Assert.NotNull(pinnedToV1.FindTool(EchoToolsManifest.ReadTool));

        var pinnedToV2 = await factory.CreateAsync(CreateSession(version2, version: 2), CancellationToken.None);
        Assert.Equal(2, pinnedToV2.Config.Version);
        Assert.Null(pinnedToV2.FindTool(EchoToolsManifest.ReadTool));
    }

    [Fact]
    public async Task Config_is_cached_for_ttl_and_reloaded_after_expiry()
    {
        var config = EchoAgentConfig.Create();
        var store = new FakeAgentConfigStore(config);
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var cache = new InMemoryAgentConfigCache(TimeSpan.FromSeconds(60), () => now);
        var factory = CreateFactory(store, cache);
        var session = CreateSession(config, version: 1);

        await factory.CreateAsync(session, CancellationToken.None);
        Assert.Equal(1, store.Loads);

        await factory.CreateAsync(session, CancellationToken.None);
        Assert.Equal(1, store.Loads);

        now = now.AddSeconds(61);
        await factory.CreateAsync(session, CancellationToken.None);
        Assert.Equal(2, store.Loads);
    }

    [Fact]
    public async Task Config_cache_is_invalidated_on_configuration_change()
    {
        var config = EchoAgentConfig.Create();
        var store = new FakeAgentConfigStore(config);
        var factory = CreateFactory(store, new InMemoryAgentConfigCache(TimeSpan.FromSeconds(60)));
        var session = CreateSession(config, version: 1);

        await factory.CreateAsync(session, CancellationToken.None);
        Assert.Equal(1, store.Loads);

        // Кабинет сохранил изменения конфигурации → инвалидация кеша агента.
        store.Set(EchoAgentConfig.WithoutTool(EchoToolsManifest.WriteTool));
        await factory.InvalidateAsync(config.TenantId, config.AgentId);

        var reloaded = await factory.CreateAsync(session, CancellationToken.None);

        Assert.Equal(2, store.Loads);
        Assert.Null(reloaded.FindTool(EchoToolsManifest.WriteTool));
    }

    [Fact]
    public async Task Missing_configuration_is_reported()
    {
        var store = new FakeAgentConfigStore();
        var factory = CreateFactory(store, new InMemoryAgentConfigCache(TimeSpan.FromSeconds(60)));
        var session = CreateSession(EchoAgentConfig.Create(), version: 7);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.CreateAsync(session, CancellationToken.None));
    }

    [Fact]
    public void Agent_config_round_trips_through_db_json_for_config_cache()
    {
        var config = EchoAgentConfig.Create();

        var json = AgentPlatform.Core.Json.PlatformJson.SerializeDb(config);
        var restored = AgentPlatform.Core.Json.PlatformJson.DeserializeDb<AgentPlatform.Core.Contracts.AgentRuntimeConfig>(json);

        Assert.NotNull(restored);
        Assert.Equal(config.Version, restored!.Version);
        Assert.Equal(config.SystemPrompt, restored.SystemPrompt);
        Assert.Equal("test-model", restored.Model.Model);
        Assert.Equal(2, restored.Tools.Count);

        var readTool = Assert.Single(restored.Tools, tool => tool.ToolName == EchoToolsManifest.ReadTool);
        Assert.Equal(ToolPolicy.Read, readTool.Tool.ManifestPolicy);
        Assert.Equal(GateMode.Never, readTool.Tool.TenantGateMode);
        Assert.Equal(EchoAgentConfig.InstallationId, readTool.InstallationId);
    }

    private static AgentFactory CreateFactory(IAgentConfigStore store, IAgentConfigCache cache)
        => new(
            store,
            cache,
            new SingleChatClientFactory(new ScriptedChatClient()),
            new AgentRuntimeOptions());

    private static SessionContext CreateSession(AgentPlatform.Core.Contracts.AgentRuntimeConfig config, int version)
        => new(
            Guid.NewGuid(),
            config.TenantId,
            config.AgentId,
            version,
            Guid.NewGuid(),
            "visitor-1");
}
