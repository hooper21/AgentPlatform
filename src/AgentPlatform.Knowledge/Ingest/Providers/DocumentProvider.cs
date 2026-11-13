using System.Globalization;
using System.Runtime.CompilerServices;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Dto;
using AgentPlatform.Knowledge.Configuration;

namespace AgentPlatform.Knowledge.Ingest.Providers;

/// <summary>
/// Провайдер document: файлы MD/TXT (MVP). PDF/DOCX появятся позже — сейчас
/// выбрасывается понятная ошибка вместо тихой индексации мусора.
/// Markdown разбивается на разделы по заголовкам: заголовок попадает в метаданные
/// каждого чанка. Провайдер отдаёт сырые страницы и курсор (время изменения файла),
/// не зная про чанкинг и эмбеддинги.
/// </summary>
public sealed class DocumentProvider(KnowledgeOptions options, TimeProvider timeProvider) : IKnowledgeProvider
{
    private static readonly string[] SupportedExtensions = [".md", ".markdown", ".txt"];

    private readonly KnowledgeOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <inheritdoc />
    public KnowledgeProviderInfo GetSourceInfo() => new(
        KnowledgeProviderTypes.Document,
        "Документ (MD/TXT)",
        KnowledgeConnectionTypes.File,
        "Markdown или текстовый файл; разбивается на разделы по заголовкам.",
        [
            new KnowledgeProviderParameter(
                KnowledgeProviderParameterNames.Location,
                "Путь к файлу",
                Required: true,
                Description: "Относительно Knowledge:DocumentRoot или абсолютный путь.")
        ],
        SupportsIncremental: true);

    /// <inheritdoc />
    public Task<KnowledgeConnectionStatus> TestConnection(
        KnowledgeProviderConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var now = _timeProvider.GetUtcNow();

        if (string.IsNullOrWhiteSpace(config.Location))
        {
            return Task.FromResult(KnowledgeConnectionStatus.Unhealthy(
                "Не указан путь к файлу.",
                now));
        }

        try
        {
            var path = ResolvePath(config.Location);
            if (!File.Exists(path))
            {
                return Task.FromResult(KnowledgeConnectionStatus.Unhealthy(
                    $"Файл не найден: '{config.Location}'.",
                    now,
                    path));
            }

            var extension = Path.GetExtension(path);
            if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return Task.FromResult(KnowledgeConnectionStatus.Unhealthy(
                    $"Тип файла '{extension}' не поддерживается: нужен MD/TXT.",
                    now));
            }

            return Task.FromResult(KnowledgeConnectionStatus.Healthy(
                $"Файл доступен: {path}.",
                now));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Task.FromResult(KnowledgeConnectionStatus.Unhealthy(
                "Не удалось проверить файл.",
                now,
                exception.Message));
        }
    }


    /// <inheritdoc />
    public async IAsyncEnumerable<KnowledgePage> FetchAll(
        KnowledgeProviderConfig config,
        DateTimeOffset? since = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.Location);

        var path = ResolvePath(config.Location);
        var extension = Path.GetExtension(path);

        if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Тип файла '{extension}' не поддерживается инжестом document в MVP. " +
                "Поддерживаются MD/TXT; PDF/DOCX будут добавлены позже.");
        }

        var modifiedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        if (since is not null && modifiedAt <= since.Value)
        {
            yield break;
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var sections = MarkdownSections.Split(text, config.Title);
        var cursor = FormatCursor(modifiedAt);
        var metadata = new KnowledgePageMetadata(
            ModifiedAt: modifiedAt,
            Url: path,
            Space: Path.GetDirectoryName(path),
            Folder: Path.GetFileName(path));
        var position = 0;

        foreach (var section in sections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new KnowledgePage(
                ExternalId: $"{Path.GetFileName(path)}#{position}",
                Title: config.Title,
                Content: section.Content,
                Metadata: metadata,
                Heading: section.Heading,
                Position: position++,
                Cursor: cursor);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<KnowledgeChange> FetchIncremental(
        KnowledgeProviderConfig config,
        string? cursor = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Курсор document — время последнего изменения файла (ISO 8601 UTC).
        var since = ParseCursor(cursor);

        await foreach (var page in FetchAll(config, since, cancellationToken).ConfigureAwait(false))
        {
            yield return new KnowledgeChange(page, page.Cursor);
        }
    }

    private string ResolvePath(string reference)
    {
        var root = string.IsNullOrWhiteSpace(_options.DocumentRoot)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(_options.DocumentRoot);

        var path = Path.IsPathRooted(reference)
            ? Path.GetFullPath(reference)
            : Path.GetFullPath(Path.Combine(root, reference));

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Document source '{reference}' is not found (resolved path: {path}).",
                path);
        }

        return path;
    }

    private static string FormatCursor(DateTimeOffset modifiedAt)
        => modifiedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseCursor(string? cursor)
        => DateTimeOffset.TryParse(
            cursor,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var value)
            ? value
            : null;
}

/// <summary>Разбиение Markdown на разделы по заголовкам (ATX: #..######).</summary>
internal static class MarkdownSections
{
    public static IReadOnlyList<(string? Heading, string Content)> Split(string text, string fallbackTitle)
    {
        ArgumentNullException.ThrowIfNull(text);

        var sections = new List<(string? Heading, string Content)>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string? currentHeading = null;
        var current = new List<string>();

        void Flush()
        {
            var content = string.Join('\n', current).Trim();
            if (content.Length > 0)
            {
                sections.Add((currentHeading, content));
            }

            current.Clear();
        }

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (TryReadHeading(trimmed, out var heading))
            {
                Flush();
                currentHeading = heading;
                continue;
            }

            current.Add(line);
        }

        Flush();

        if (sections.Count == 0 && !string.IsNullOrWhiteSpace(text))
        {
            sections.Add((null, text.Trim()));
        }

        // Первый раздел без заголовка подписываем названием источника.
        if (sections.Count > 0 && sections[0].Heading is null)
        {
            sections[0] = (fallbackTitle, sections[0].Content);
        }

        return sections;
    }

    private static bool TryReadHeading(string line, out string heading)
    {
        heading = string.Empty;
        var level = 0;

        while (level < line.Length && level < 6 && line[level] == '#')
        {
            level++;
        }

        if (level == 0 || level >= line.Length || line[level] != ' ')
        {
            return false;
        }

        var value = line[(level + 1)..].Trim().TrimEnd('#').Trim();
        if (value.Length == 0)
        {
            return false;
        }

        heading = value;
        return true;
    }
}
