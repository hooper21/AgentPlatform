using AgentPlatform.Core.Contracts;
using Microsoft.Extensions.AI;

namespace AgentPlatform.LlmGateway;

/// <summary>
/// Детерминированный клиент модели для демонстрационного сквозного сценария
/// (сообщение → вызов echo_write → пауза → подтверждение → исполнение).
/// <para>
/// Реальный провайдер подключается в LLM Gateway (фаза 2). Эта реализация нужна,
/// чтобы гейт-ревью фазы 1 работало без ключей внешней модели: команды
/// <c>write: &lt;текст&gt;</c> и <c>read: &lt;текст&gt;</c> превращаются в вызовы
/// тулов <c>echo_write</c> / <c>echo_read</c> из манифеста EchoTools.
/// </para>
/// <para>
/// Тул вызывается только когда последнее сообщение принадлежит пользователю:
/// после результата тула/отказа/решения гейта модель отвечает текстом и не
/// зацикливает повторный вызов.
/// </para>
/// </summary>
public sealed class DemoChatClient(string model = "demo-model") : IChatClient
{
    /// <summary>Тул записи тестового коннектора (write-политика, всегда через гейт).</summary>
    public const string WriteTool = "echo_write";

    /// <summary>Тул чтения тестового коннектора (read-политика).</summary>
    public const string ReadTool = "echo_read";

    /// <summary>Префикс команды записи для демонстрационного сценария.</summary>
    public const string WriteCommand = "write:";

    /// <summary>Префикс команды чтения для демонстрационного сценария.</summary>
    public const string ReadCommand = "read:";

    private readonly string _model = model;

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();

        var history = messages.ToList();
        var last = history.LastOrDefault();

        // Результат тула уже в истории: сообщаем его пользователю.
        if (last?.Role == ChatRole.Tool)
        {
            return Reply($"Готово: {last.Text}");
        }

        // После отказа/решения гейта или ответа ассистента новых команд не исполняем.
        if (last?.Role != ChatRole.User)
        {
            return Reply("Диалог продолжен без нового вызова тула.");
        }

        var text = last.Text ?? string.Empty;
        var toolNames = options?.Tools?
            .OfType<AIFunctionDeclaration>()
            .Select(tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal) ?? [];

        if (toolNames.Contains(WriteTool)
            && (TryExtractPayload(text, WriteCommand, out var writePayload)
                || text.Contains("запиши", StringComparison.OrdinalIgnoreCase)))
        {
            return CallTool(WriteTool, writePayload);
        }

        if (toolNames.Contains(ReadTool)
            && (TryExtractPayload(text, ReadCommand, out var readPayload)
                || text.Contains("прочитай", StringComparison.OrdinalIgnoreCase)))
        {
            return CallTool(ReadTool, readPayload);
        }

        return Reply(
            $"[demo:{_model}] Принято: {text}. " +
            $"Для записи через гейт отправьте '{WriteCommand} <текст>', для чтения — '{ReadCommand} <текст>'.");
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Демонстрационный клиент не поддерживает стриминг от провайдера.
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Внешних ресурсов нет.
    }

    private static Task<ChatResponse> Reply(string text)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

    private static Task<ChatResponse> CallTool(string toolName, string message)
    {
        var call = new FunctionCallContent(
            Guid.NewGuid().ToString("N"),
            toolName,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["message"] = message });

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
    }

    private static bool TryExtractPayload(string text, string command, out string payload)
    {
        payload = text.Trim();

        if (!payload.StartsWith(command, StringComparison.OrdinalIgnoreCase))
        {
            payload = string.Empty;
            return false;
        }

        payload = payload[command.Length..].Trim();
        return true;
    }
}

/// <summary>
/// Фабрика демонстрационного клиента модели (замена StubChatClient для Controller).
/// TODO(2.x) contract: реальный LLM Gateway вместо детерминированного клиента.
/// </summary>
public sealed class DemoChatClientFactory : IChatClientFactory
{
    /// <inheritdoc />
    public IChatClient Create(AgentModelRef modelRef)
    {
        ArgumentNullException.ThrowIfNull(modelRef);

        return new PlatformChatClient(new DemoChatClient(modelRef.Model));
    }
}
