using System.Text.Json;
using AgentPlatform.Core.Enums;
using AgentPlatform.Knowledge.Configuration;
using AgentPlatform.Knowledge.Contracts;

namespace AgentPlatform.Tests.Knowledge;

/// <summary>
/// Round-trip инжеста: FAQ и document → ready, чанки с метаданными (источник,
/// заголовок, позиция), поиск возвращает источник с title/source_type.
/// </summary>
public sealed class KnowledgeIngestTests
{
    [Fact]
    public async Task Faq_source_round_trip_is_ready_and_searchable()
    {
        using var harness = new KnowledgeHarness(KnowledgeHarness.TenantA);

        var source = await harness.Ingest.CreateAsync(new CreateKnowledgeSourceRequest(
            "faq",
            Ref: null,
            Title: "Возврат товара",
            Content: "Вернуть товар можно в течение 14 дней после покупки. Возврат денег — 3 дня."));

        Assert.Equal(KnowledgeIndexStatus.Ready.ToDbValue(), source.IndexStatus);
        Assert.True(source.ChunkCount > 0);
        Assert.Equal(string.Empty, source.Ref);
        Assert.Contains("\"last_indexed_at\"", source.IndexMeta, StringComparison.Ordinal);

        var hits = await harness.Search.SearchAsync(
            KnowledgeHarness.TenantA,
            "возврат товара",
            topK: 5,
            threshold: 0.1);

        var hit = Assert.Single(hits);
        Assert.Equal(source.Id, hit.SourceId);
        Assert.Equal("Возврат товара", hit.Title);
        Assert.Equal(KnowledgeSourceType.Faq.ToDbValue(), hit.SourceType);
        Assert.Contains("14 дней", hit.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Document_is_chunked_by_500_tokens_with_50_overlap_and_metadata()
    {
        var root = Directory.CreateTempSubdirectory("knowledge-doc-");
        try
        {
            var words = string.Join(' ', Enumerable.Range(0, 1200).Select(index => $"слово{index}"));
            var path = Path.Combine(root.FullName, "policy.md");
            await File.WriteAllTextAsync(
                path,
                $"# Политика возврата\n{words}\n\n## Сроки\nКомпенсация зачисляется за три дня.\n");

            var options = new KnowledgeOptions { DocumentRoot = root.FullName };
            using var harness = new KnowledgeHarness(
                KnowledgeHarness.TenantA,
                embedder: null,
                options: options,
                providers: [new AgentPlatform.Knowledge.Ingest.Providers.DocumentProvider(options, TimeProvider.System)]);

            var source = await harness.Ingest.CreateAsync(new CreateKnowledgeSourceRequest(
                "document",
                Ref: "policy.md",
                Title: "Политика возврата"));

            Assert.Equal(KnowledgeIndexStatus.Ready.ToDbValue(), source.IndexStatus);

            var chunks = harness.Store.GetSourceChunks(KnowledgeHarness.TenantA, source.Id);
            Assert.True(chunks.Count >= 3);
            Assert.Equal(chunks.Count, source.ChunkCount);

            static int CountTokens(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

            Assert.Equal(500, CountTokens(chunks[0].Content));
            Assert.Equal(500, CountTokens(chunks[1].Content));
            Assert.True(CountTokens(chunks[2].Content) <= 500);

            // Перекрытие 50 токенов: последние 50 слов первого чанка есть в начале второго.
            var firstTail = string.Join(' ', chunks[0].Content.Split(' ')[^50..]);
            Assert.StartsWith(firstTail, chunks[1].Content, StringComparison.Ordinal);

            using var metadata = JsonDocument.Parse(chunks[0].Metadata);
            Assert.Equal("Политика возврата", metadata.RootElement.GetProperty("source").GetString());
            Assert.Equal("document", metadata.RootElement.GetProperty("source_type").GetString());
            Assert.Equal("Политика возврата", metadata.RootElement.GetProperty("heading").GetString());
            Assert.Equal(0, metadata.RootElement.GetProperty("position").GetInt32());

            using var secondMetadata = JsonDocument.Parse(chunks[1].Metadata);
            Assert.Equal(1, secondMetadata.RootElement.GetProperty("position").GetInt32());

            using var lastMetadata = JsonDocument.Parse(chunks[^1].Metadata);
            Assert.Equal("Сроки", lastMetadata.RootElement.GetProperty("heading").GetString());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Reindex_replaces_chunks_without_duplicates()
    {
        using var harness = new KnowledgeHarness(KnowledgeHarness.TenantA);
        var source = await harness.Ingest.CreateAsync(new CreateKnowledgeSourceRequest(
            "faq",
            Ref: null,
            Title: "Доставка",
            Content: "Доставка курьером по Москве — один день."));

        var before = harness.Store.GetSourceChunks(KnowledgeHarness.TenantA, source.Id).Count;
        var reindexed = await harness.Ingest.ReindexAsync(source.Id);

        Assert.NotNull(reindexed);
        Assert.Equal(KnowledgeIndexStatus.Ready.ToDbValue(), reindexed.IndexStatus);
        Assert.Equal(before, harness.Store.GetSourceChunks(KnowledgeHarness.TenantA, source.Id).Count);
        Assert.Equal(before, await harness.Store.CountAsync(KnowledgeHarness.TenantA, source.Id));
    }
}
