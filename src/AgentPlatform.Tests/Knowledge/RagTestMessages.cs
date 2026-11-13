using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Dto;
using Microsoft.Extensions.AI;

namespace AgentPlatform.Tests.Knowledge;

/// <summary>Общие фабрики для тестов RAG в Runtime.</summary>
internal static class RagTestMessages
{
    public static IKnowledgeContextProvider FixedProvider(Func<Guid, string, KnowledgeContext> factory)
        => new FixedKnowledgeContextProvider(factory);

    public static ChatResponse Reply(string text)
        => new(new ChatMessage(ChatRole.Assistant, text));

    public static ChatResponse CallTool(string name, string message)
    {
        var call = new FunctionCallContent(
            Guid.NewGuid().ToString("N"),
            name,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["message"] = message });

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, [call]));
    }

    internal sealed class FixedKnowledgeContextProvider(
        Func<Guid, string, KnowledgeContext> factory) : IKnowledgeContextProvider
    {
        public Task<KnowledgeContext> GetContextAsync(
            Guid tenantId,
            string query,
            CancellationToken cancellationToken = default)
            => Task.FromResult(factory(tenantId, query));
    }

    internal sealed class ThrowingKnowledgeContextProvider : IKnowledgeContextProvider
    {
        public Task<KnowledgeContext> GetContextAsync(
            Guid tenantId,
            string query,
            CancellationToken cancellationToken = default)
            => throw new HttpRequestException("Knowledge Service is unavailable.");
    }

    internal sealed class FixedResultToolInvoker(string content) : IToolInvoker
    {
        public Task<ToolInvocationResult> InvokeAsync(
            ToolInvocationRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ToolInvocationResult(true, content));
    }
}
