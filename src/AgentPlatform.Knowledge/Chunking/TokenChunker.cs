using System.Text.RegularExpressions;

namespace AgentPlatform.Knowledge.Chunking;

/// <summary>Чанк, готовый к эмбеддингу и записи в knowledge_chunks.</summary>
/// <param name="Content">Текст чанка (исходное форматирование сохранено).</param>
/// <param name="Heading">Заголовок раздела, к которому относится чанк.</param>
/// <param name="Position">Позиция чанка внутри источника (0-based).</param>
/// <param name="TokenCount">Число токенов чанка.</param>
public sealed record KnowledgeChunkDraft(string Content, string? Heading, int Position, int TokenCount);

/// <summary>
/// Чанкер знаний: 500 токенов с перекрытием 50 (значения по умолчанию из
/// <see cref="Configuration.KnowledgeOptions"/>). Токен — непробельная
/// последовательность (слово/число/пунктуация); разбиение идёт по исходным
/// границам текста, поэтому форматирование и заголовки не теряются.
/// </summary>
public static partial class TokenChunker
{
    /// <summary>Разбивает текст на чанки с перекрытием.</summary>
    /// <param name="text">Текст страницы/раздела.</param>
    /// <param name="heading">Заголовок раздела (попадает в метаданные каждого чанка).</param>
    /// <param name="maxTokens">Максимум токенов в чанке.</param>
    /// <param name="overlapTokens">Перекрытие соседних чанков в токенах.</param>
    public static IReadOnlyList<KnowledgeChunkDraft> Split(
        string text,
        string? heading,
        int maxTokens,
        int overlapTokens)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (maxTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTokens), "Chunk size must be positive.");
        }

        if (overlapTokens < 0 || overlapTokens >= maxTokens)
        {
            throw new ArgumentOutOfRangeException(
                nameof(overlapTokens),
                "Overlap must be in [0, chunk size).");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var tokens = TokenPattern().Matches(text);
        if (tokens.Count == 0)
        {
            return [];
        }

        var chunks = new List<KnowledgeChunkDraft>();
        var start = 0;

        while (start < tokens.Count)
        {
            var end = Math.Min(start + maxTokens, tokens.Count);
            var first = tokens[start];
            var last = tokens[end - 1];
            var content = text[first.Index..(last.Index + last.Length)].Trim();

            if (content.Length > 0)
            {
                chunks.Add(new KnowledgeChunkDraft(content, heading, chunks.Count, end - start));
            }

            if (end == tokens.Count)
            {
                break;
            }

            start = end - overlapTokens;
        }

        return chunks;
    }

    [GeneratedRegex(@"\S+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
