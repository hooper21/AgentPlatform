using System.Runtime.CompilerServices;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;

namespace AgentPlatform.Knowledge.Ingest.Providers;

/// <summary>
/// Провайдер faq: ручная запись (тип faq, ref = null, content в теле запроса).
/// Одна FAQ-пара — одна страница источника.
/// </summary>
public sealed class FaqKnowledgeProvider : IKnowledgeProvider
{
    /// <inheritdoc />
    public KnowledgeSourceType SourceType => KnowledgeSourceType.Faq;

    /// <inheritdoc />
    public async IAsyncEnumerable<KnowledgePage> ReadAsync(
        KnowledgeProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Content))
        {
            throw new ArgumentException(
                "Для источника type=faq обязателен content в теле запроса.",
                nameof(request));
        }

        await Task.CompletedTask.ConfigureAwait(false);

        yield return new KnowledgePage(
            request.Title,
            request.Content.Trim(),
            Heading: request.Title,
            Position: 0);
    }
}
