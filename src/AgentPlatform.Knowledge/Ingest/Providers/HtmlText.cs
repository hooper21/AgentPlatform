using System.Net;
using System.Text.RegularExpressions;

namespace AgentPlatform.Knowledge.Ingest.Providers;

/// <summary>
/// Извлечение текста/ссылок из HTML без внешних парсеров: MVP-обход одного
/// уровня страниц сайта. Разметка удаляется, сущности декодируются.
/// </summary>
internal static partial class HtmlText
{
    /// <summary>Заголовок страницы (title) или первый h1.</summary>
    public static string ExtractTitle(string html, string fallback)
    {
        ArgumentNullException.ThrowIfNull(html);

        var title = TitlePattern().Match(html);
        if (title.Success)
        {
            var value = WebUtility.HtmlDecode(StripTags(title.Groups[1].Value)).Trim();
            if (value.Length > 0)
            {
                return value;
            }
        }

        var heading = HeadingPattern().Match(html);
        if (heading.Success)
        {
            var value = WebUtility.HtmlDecode(StripTags(heading.Groups[1].Value)).Trim();
            if (value.Length > 0)
            {
                return value;
            }
        }

        return fallback;
    }

    /// <summary>Абсолютные http(s)-ссылки страницы (без фрагментов, дубликатов и якорей).</summary>
    public static IReadOnlyList<Uri> ExtractLinks(string html, Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(baseUri);

        var links = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in LinkPattern().Matches(html))
        {
            var href = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
            if (href.Length == 0
                || href.StartsWith('#')
                || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Uri.TryCreate(baseUri, href, out var absolute)
                || (absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }

            var normalized = new UriBuilder(absolute) { Fragment = string.Empty }.Uri;
            if (seen.Add(normalized.AbsoluteUri))
            {
                links.Add(normalized);
            }
        }

        return links;
    }

    /// <summary>HTML → plain text: script/style удаляются, блочные теги дают переводы строк.</summary>
    public static string ToPlainText(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var cleaned = ScriptStylePattern().Replace(html, " ");
        cleaned = BlockBreakPattern().Replace(cleaned, "\n");
        cleaned = TagPattern().Replace(cleaned, " ");
        cleaned = WebUtility.HtmlDecode(cleaned);
        cleaned = WhitespacePattern().Replace(cleaned, " ");
        cleaned = NewLinePattern().Replace(cleaned, "\n\n");

        return cleaned.Trim();
    }

    private static string StripTags(string html) => TagPattern().Replace(html, " ");

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitlePattern();

    [GeneratedRegex(@"<h1[^>]*>(.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"<a\s+[^>]*href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptStylePattern();

    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/h[1-6]|/tr)\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreakPattern();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"\n\s*\n+")]
    private static partial Regex NewLinePattern();
}
