using System.Text.RegularExpressions;

namespace AgentPlatform.Knowledge.Ingest.Providers.Confluence;

/// <summary>
/// Приведение Confluence storage format к чистому тексту. Медиа (изображения,
/// вложения) не индексируются: TODO фазы 4 — OCR/extraction бинарных вложений.
/// Служебные узлы (ac:*, ri:*) удаляются вместе с параметрами макросов;
/// пользовательский текст остаётся данными и не интерпретируется.
/// </summary>
internal static partial class ConfluenceStorageText
{
    /// <summary>Storage/HTML → plain text без разметки и макросов.</summary>
    public static string ToPlainText(string? storage)
    {
        if (string.IsNullOrWhiteSpace(storage))
        {
            return string.Empty;
        }

        var cleaned = CommentPattern().Replace(storage, " ");
        // Параметры макросов (заголовки, фильтры) — не контент страницы.
        cleaned = MacroParameterPattern().Replace(cleaned, " ");
        // Изображения/вложения не индексируются в MVP (медиа — фаза 4).
        cleaned = MediaPattern().Replace(cleaned, " ");

        return HtmlText.ToPlainText(cleaned);
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentPattern();

    [GeneratedRegex(
        @"<ac:parameter\b[^>]*>.*?</ac:parameter\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MacroParameterPattern();

    [GeneratedRegex(
        @"<ac:(image|attachment|emoticon)\b[^>]*>.*?</ac:\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MediaPattern();
}
