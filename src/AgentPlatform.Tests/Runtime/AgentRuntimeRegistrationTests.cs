using AgentPlatform.ConnectorHost.Tools;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;
using AgentPlatform.Infrastructure.Repositories;
using AgentPlatform.LlmGateway;
using AgentPlatform.Runtime;
using AgentPlatform.Runtime.Orchestration;
using AgentPlatform.Runtime.Policy;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPlatform.Tests.Runtime;

/// <summary>
/// Регистрация Runtime в DI: оркестратор, AgentFactory, кеш конфигурации и Policy Engine
/// собираются из адаптеров, которые регистрирует хост.
/// </summary>
public sealed class AgentRuntimeRegistrationTests
{
    [Fact]
    public void Runtime_services_are_resolved_from_di()
    {
        var services = new ServiceCollection();
        services.AddMafAgentRuntime(new AgentRuntimeOptions());

        services.AddSingleton<IAgentConfigStore>(new FakeAgentConfigStore(EchoAgentConfig.Create()));
        services.AddSingleton<IChatClientFactory>(new SingleChatClientFactory(new ScriptedChatClient()));
        services.AddSingleton<ICheckpointStore>(new InMemoryCheckpointStore());
        services.AddSingleton<IApprovalStore>(new InMemoryApprovalStore());
        services.AddSingleton<IToolInvoker>(new EchoToolsInvoker());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<MafOrchestrationRuntime>(scope.ServiceProvider.GetRequiredService<IOrchestrationRuntime>());
        Assert.NotNull(provider.GetRequiredService<AgentFactory>());
        Assert.NotNull(provider.GetRequiredService<IAgentConfigCache>());
        Assert.Same(
            provider.GetRequiredService<PolicyEngine>(),
            provider.GetRequiredService<IPolicyEngine>());
    }
}
