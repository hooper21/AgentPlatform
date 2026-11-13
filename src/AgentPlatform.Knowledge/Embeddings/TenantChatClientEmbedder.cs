using System.Text.Json;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Core.Json;
using AgentPlatform.Infrastructure.Persistence.Entities;
using AgentPlatform.Knowledge.Configuration;
using AgentPlatform.LlmGateway;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentPlatform.Knowledge.Embeddings;

/// <summary>
/// Эмбеддер через <see cref="IChatClient"/>: модель и endpoint берутся из
/// llm_config тенанта, клиент создаёт фабрика LLM Gateway.
/// <para>
/// MVP (Ollama nomic-embed-text, dim=768): если IChatClient умеет эмбеддинги
/// (отдаёт <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> через GetService),
/// используется нативный путь. Иначе — chat-протокол «верни JSON-массив чисел»
/// (пометка TODO(llm-gateway): провайдерский эмбеддинг вместо chat-фолбэка).
/// </para>
/// <para>
/// Размерность проверяется до записи: несовпадение — исключение
/// <see cref="EmbeddingDimensionMismatchException"/>, данные не подгоняются.
/// </para>
/// </summary>
public sealed class TenantChatClientEmbedder(
    IChatClientFactory chatClientFactory,
    IGlobalRepository<Tenant> tenants,
    KnowledgeOptions options,
    ILogger<TenantChatClientEmbedder> logger) : IKnowledgeEmbedder
{
    private readonly IChatClientFactory _chatClientFactory =
        chatClientFactory ?? throw new ArgumentNullException(nameof(chatClientFactory));

    private readonly IGlobalRepository<Tenant> _tenants =
        tenants ?? throw new ArgumentNullException(nameof(tenants));

    private readonly KnowledgeOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    private readonly ILogger<TenantChatClientEmbedder> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public int Dimensions => _options.EmbeddingDimensions;

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<float>> EmbedAsync(
        Guid tenantId,
        string text,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("tenant_id is required for embeddings", nameof(tenantId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var modelRef = await ResolveModelAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var chatClient = _chatClientFactory.Create(modelRef);

        var vector = await GenerateVectorAsync(chatClient, text, cancellationToken).ConfigureAwait(false);

        if (vector.Length != Dimensions)
        {
            throw new EmbeddingDimensionMismatchException(
                $"Размерность эмбеддинга {vector.Length} не совпадает с knowledge_chunks.embedding " +
                $"vector({Dimensions}) (модель '{modelRef.Model}'). Индексирование остановлено: " +
                "векторы не обрезаются и не дополняются.",
                vector.Length,
                Dimensions);
        }

        return vector;
    }

    private async Task<AgentModelRef> ResolveModelAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        // llm_config тенанта: endpoint собственной модели (mode = own) и её имя.
        var tenant = await _tenants.FindAsync(tenantId, cancellationToken).ConfigureAwait(false);
        string? endpoint = null;

        if (tenant is not null && !string.IsNullOrWhiteSpace(tenant.LlmConfig))
        {
            try
            {
                var llmConfig = PlatformJson.DeserializeDb<TenantLlmConfig>(tenant.LlmConfig);
                endpoint = llmConfig?.Endpoint;
            }
            catch (JsonException exception)
            {
                // Конфигурация проверяется при сохранении тенанта (fail-safe здесь).
                _logger.LogWarning(
                    exception,
                    "Tenant {TenantId} has invalid llm_config; platform endpoint is used for embeddings",
                    tenantId);
            }
        }

        return new AgentModelRef(_options.EmbeddingModel, endpoint);
    }

    private async Task<float[]> GenerateVectorAsync(
        IChatClient chatClient,
        string text,
        CancellationToken cancellationToken)
    {
        var generator = chatClient.GetService(typeof(IEmbeddingGenerator<string, Embedding<float>>))
            as IEmbeddingGenerator<string, Embedding<float>>;

        if (generator is not null)
        {
            var generated = await generator
                .GenerateAsync(
                    [text],
                    new EmbeddingGenerationOptions { Dimensions = Dimensions },
                    cancellationToken)
                .ConfigureAwait(false);

            return generated[0].Vector.ToArray();
        }

        // TODO(llm-gateway): нативный эмбеддинг провайдера через LLM Gateway.
        // Пока провайдер не отдаёт IEmbeddingGenerator, используется chat-протокол.
        var response = await chatClient
            .GetResponseAsync(
                [
                    new ChatMessage(
                        ChatRole.System,
                        $"Ты — генератор эмбеддингов. Верни ровно {Dimensions} чисел (float) " +
                        "одним JSON-массивом без пояснений и markdown."),
                    new ChatMessage(ChatRole.User, text)
                ],
                new ChatOptions { Temperature = 0f, MaxOutputTokens = Math.Max(1024, Dimensions * 8) },
                cancellationToken)
            .ConfigureAwait(false);

        return EmbeddingVectorParser.Parse(response.Text, Dimensions);
    }
}
