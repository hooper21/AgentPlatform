using AgentPlatform.Knowledge.Embeddings;

namespace AgentPlatform.Tests.Knowledge;

/// <summary>Разбор вектора из ответа chat-модели и проверка размерности.</summary>
public sealed class EmbeddingVectorParserTests
{
    [Fact]
    public void Parser_reads_json_array_wrapped_in_markdown()
    {
        var vector = EmbeddingVectorParser.Parse("вот вектор:\n```json\n[0.5, -0.25, 1]\n```", 3);

        Assert.Equal([0.5f, -0.25f, 1f], vector);
    }

    [Fact]
    public void Parser_rejects_dimension_mismatch()
    {
        var exception = Assert.Throws<EmbeddingDimensionMismatchException>(
            () => EmbeddingVectorParser.Parse("[0.1, 0.2]", expectedDimensions: 768));

        Assert.Equal(2, exception.Actual);
        Assert.Equal(768, exception.Expected);
        Assert.Contains("не совпадает", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_rejects_response_without_array()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => EmbeddingVectorParser.Parse("не могу выдать вектор", expectedDimensions: 768));

        Assert.Contains("JSON-массив", exception.Message, StringComparison.Ordinal);
    }
}
