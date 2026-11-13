using System.Runtime.CompilerServices;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;
using AgentPlatform.Knowledge.Configuration;

namespace AgentPlatform.Knowledge.Ingest.Providers;

/// <summary>
/// Провайдер document: файлы MD/TXT (MVP). PDF/DOCX появятся позже — сейчас
/// выбрасывается понятная ошибка вместо тихой индексации мусора.
/// Markdown разбивается на разделы по заголовкам: заголовок попадает в метаданные
/// каждого чанка.
/// </summary>
public sealed class DocumentKnowledgeProvider(KnowledgeOptions options) : IKnowledgeProvider
{
    private static readonly string[] SupportedExtensions = [".md", ".markdown", ".txt"];

    private readonly KnowledgeOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public KnowledgeSourceType SourceType => KnowledgeSourceType.Document;

    /// <inheritdoc />
    public async IAsyncEnumerable<KnowledgePage> ReadAsync(
        KnowledgeProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Ref);

        var path = ResolvePath(request.Ref);
        var extension = Path.GetExtension(path);

        if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Тип файла '{extension}' не поддерживается инжестом document в MVP. " +
                "Поддерживаются MD/TXT; PDF/DOCX будут добавлены позже.");
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var sections = MarkdownSections.Split(text, request.Title);
        var position = 0;

        foreach (var section in sections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new KnowledgePage(request.Title, section.Content, section.Heading, position++);
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
