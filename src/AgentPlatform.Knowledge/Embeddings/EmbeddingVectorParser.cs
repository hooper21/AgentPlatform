using System.Text.Json;

namespace AgentPlatform.Knowledge.Embeddings;

/// <summary>Разбор вектора из ответа chat-модели (JSON-массив чисел).</summary>
public static class EmbeddingVectorParser
{
    /// <summary>Парсит JSON-массив чисел; markdown-обёртка допускается.</summary>
    public static float[] Parse(string? text, int expectedDimensions)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                "Эмбеддер вернул пустой ответ: ожидался JSON-массив чисел.");
        }

        var start = text.IndexOf('[', StringComparison.Ordinal);
        var end = text.LastIndexOf(']');

        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException(
                $"Эмбеддер вернул ответ без JSON-массива: {Truncate(text)}");
        }

        var json = text[start..(end + 1)];

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("Эмбеддер вернул не JSON-массив.");
            }

            var vector = new float[document.RootElement.GetArrayLength()];
            var index = 0;

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out var value))
                {
                    throw new InvalidOperationException(
                        "Эмбеддер вернул массив с нечисловым элементом.");
                }

                if (double.IsNaN(value) || double.IsInfinity(value))
                {
                    throw new InvalidOperationException("Эмбеддер вернул NaN/Infinity.");
                }

                vector[index++] = (float)value;
            }

            if (vector.Length != expectedDimensions)
            {
                throw new EmbeddingDimensionMismatchException(
                    $"Размерность эмбеддинга {vector.Length} не совпадает с ожидаемой " +
                    $"{expectedDimensions} (knowledge_chunks.embedding).",
                    vector.Length,
                    expectedDimensions);
            }

            return vector;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Эмбеддер вернул некорректный JSON-массив: {Truncate(text)}",
                exception);
        }
    }

    private static string Truncate(string text)
        => text.Length <= 200 ? text : string.Concat(text.AsSpan(0, 200), "…");
}
