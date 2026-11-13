using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Dto;

namespace AgentPlatform.Knowledge.Ingest.Providers;

/// <summary>
/// Провайдер faq: ручная запись (content в config, location = null).
/// Одна FAQ-пара — одна страница источника. Курсор — SHA-256 контента:
/// если контент не изменился, инкрементальная синхронизация не даёт изменений.
/// </summary>
public sealed class FaqProvider(TimeProvider timeProvider) : IKnowledgeProvider
{
    /// <summary>Стабильный externalId единственной страницы FAQ.</summary>
    public const string PageExternalId = "faq";

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <inheritdoc />
    public KnowledgeProviderInfo GetSourceInfo() => new(
        KnowledgeProviderTypes.Faq,
        "FAQ-запись",
        KnowledgeConnectionTypes.Manual,
        "Текст вопроса и ответа вводится вручную в кабинете.",
        [
            new KnowledgeProviderParameter(
                KnowledgeProviderParameterNames.Content,
                "Текст записи",
                Required: true,
                Description: "Вопрос и ответ; попадут в чанки как единая страница.")
        ],
        SupportsIncremental: true);

    /// <inheritdoc />
    public Task<KnowledgeConnectionStatus> TestConnection(
        KnowledgeProviderConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var now = _timeProvider.GetUtcNow();

        return Task.FromResult(string.IsNullOrWhiteSpace(config.Content)
            ? KnowledgeConnectionStatus.Unhealthy("Не заполнен текст FAQ-записи.", now)
            : KnowledgeConnectionStatus.Healthy("Текст FAQ-записи заполнен.", now));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<KnowledgePage> FetchAll(
        KnowledgeProviderConfig config,
        DateTimeOffset? since = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrWhiteSpace(config.Content))
        {
            throw new ArgumentException(
                "Для источника type=faq обязателен content в теле запроса.",
                nameof(config));
        }

        await Task.CompletedTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var content = config.Content.Trim();

        yield return new KnowledgePage(
            ExternalId: PageExternalId,
            Title: config.Title,
            Content: content,
            Metadata: new KnowledgePageMetadata(Space: "FAQ", Folder: config.Title),
            Heading: config.Title,
            Position: 0,
            Cursor: ComputeCursor(content));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<KnowledgeChange> FetchIncremental(
        KnowledgeProviderConfig config,
        string? cursor = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);

        await foreach (var page in FetchAll(config, since: null, cancellationToken).ConfigureAwait(false))
        {
            // Неизменившийся контент — не изменение: курсор остаётся прежним.
            if (!string.Equals(page.Cursor, cursor, StringComparison.Ordinal))
            {
                yield return new KnowledgeChange(page, page.Cursor);
            }
        }
    }

    private static string ComputeCursor(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
