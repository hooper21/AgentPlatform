using AgentPlatform.Core.Contracts;
using Microsoft.Extensions.AI;

namespace AgentPlatform.Runtime.Orchestration;

/// <summary>
/// Собранный AgentFactory «агент» для одного хода: закреплённая версия конфигурации
/// (промпт, модель, активные тулы), клиент модели и декларации тулов для LLM.
/// <para>
/// Тулы передаются модели только как декларации (<see cref="AIFunctionDeclaration"/>):
/// исполняет их Runtime через gate/tool-узлы, а не LLM-клиент. Иначе политики
/// write/destructive могли бы обойти HITL-гейт.
/// </para>
/// </summary>
/// <param name="Config">Конфигурация закреплённой версии агента.</param>
/// <param name="ChatClient">Клиент модели (LLM Gateway).</param>
/// <param name="ToolDeclarations">Декларации активных тулов для модели.</param>
public sealed record PlatformAgent(
    AgentRuntimeConfig Config,
    IChatClient ChatClient,
    IReadOnlyList<AIFunctionDeclaration> ToolDeclarations)
{
    /// <summary>Ищет активный тул по имени из манифеста (null — тул не подключён).</summary>
    public AgentToolConfig? FindTool(string toolName)
        => Config.Tools.FirstOrDefault(tool => string.Equals(tool.ToolName, toolName, StringComparison.Ordinal));
}
