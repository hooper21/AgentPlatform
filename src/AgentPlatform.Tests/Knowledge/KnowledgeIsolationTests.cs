using AgentPlatform.Core.Enums;
using AgentPlatform.Knowledge.Contracts;

namespace AgentPlatform.Tests.Knowledge;

/// <summary>
/// Изоляция тенантов и жизненный цикл источника: чужие данные не видны ни в
/// источниках, ни в поиске; пустой результат — пустой список + rag_misses.
/// </summary>
public sealed class KnowledgeIsolationTests
{
    [Fact]
    public async Task Tenant_B_cannot_read_or_search_tenant_A_sources()
    {
        using var harness = new KnowledgeHarness(KnowledgeHarness.TenantA);

        var sourceA = await harness.Ingest.CreateAsync(new CreateKnowledgeSourceRequest(
            "faq",
            Ref: null,
            Title: "Секрет A",
            Content: "Внутренние условия тенанта A: скидка 25 процентов."));

        harness.SwitchTenant(KnowledgeHarness.TenantB);

        Assert.Null(await harness.Sources.FindAsync(sourceA.Id));

        var sourceB = await harness.Ingest.CreateAsync(new CreateKnowledgeSourceRequest(
            "faq",
            Ref: null,
            Title: "Данные B",
            Content: "Условия тенанта B: доставка бесплатно."));

        Assert.NotEqual(sourceA.Id, sourceB.Id);

        var hitsB = await harness.Search.SearchAsync(
            KnowledgeHarness.TenantB,
            "скидка 25 процентов",
            topK: 5,
            threshold: 0.1);

        Assert.DoesNotContain(hitsB, hit => hit.SourceId == sourceA.Id);
        Assert.All(hitsB, hit => Assert.Equal(sourceB.Id, hit.SourceId));

        // Чанки tenant A недоступны и через хранилище: фильтр по tenant_id.
        Assert.Empty(harness.Store.GetSourceChunks(KnowledgeHarness.TenantB, sourceA.Id));
        Assert.Empty(await harness.Store.SearchAsync(
            KnowledgeHarness.TenantB,
            TestKnowledgeEmbedder.Vectorize("скидка 25 процентов", 768),
            topK: 5,
            threshold: 0.1));
    }

    [Fact]
    public async Task Empty_search_result_returns_empty_list_and_logs_rag_miss()
    {
        using var harness = new KnowledgeHarness(KnowledgeHarness.TenantA);
        await harness.Ingest.CreateAsync(new CreateKnowledgeSourceRequest(
            "faq",
            Ref: null,
            Title: "Доставка",
            Content: "Доставка по Москве — один день."));

        var hits = await harness.Search.SearchAsync(
            KnowledgeHarness.TenantA,
            "ипотечный кредит для юридических лиц",
            topK: 5,
            threshold: 0.77);

        Assert.Empty(hits);

        var miss = Assert.Single(harness.Misses.Entries);
        Assert.Equal(KnowledgeHarness.TenantA, miss.TenantId);
        Assert.Contains("ипотечный", miss.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_without_sources_at_all_returns_empty_list()
    {
        using var harness = new KnowledgeHarness(KnowledgeHarness.TenantA);

        var hits = await harness.Search.SearchAsync(
            KnowledgeHarness.TenantA,
            "любой вопрос",
            topK: 5,
            threshold: 0.77);

        Assert.Empty(hits);
        Assert.Single(harness.Misses.Entries);
    }

    [Fact]
    public async Task Embedding_dimension_mismatch_marks_source_error_and_keeps_it()
    {
        using var harness = new KnowledgeHarness(
            KnowledgeHarness.TenantA,
            embedder: new TestKnowledgeEmbedder(dimensions: 512));

        var source = await harness.Ingest.CreateAsync(new CreateKnowledgeSourceRequest(
            "faq",
            Ref: null,
            Title: "Проверка размерности",
            Content: "Размерность вектора должна совпадать с knowledge_chunks.embedding."));

        Assert.Equal(KnowledgeIndexStatus.Error.ToDbValue(), source.IndexStatus);
        Assert.Equal(0, source.ChunkCount);

        var meta = KnowledgeIndexMeta.Parse(source.IndexMeta);
        Assert.Contains("512", meta.Error, StringComparison.Ordinal);
        Assert.Contains("768", meta.Error, StringComparison.Ordinal);
        Assert.Contains("не совпадает", meta.Error, StringComparison.Ordinal);

        // Источник не удаляется: ошибка видна в index_meta и доступна для reindex.
        var stored = await harness.Sources.FindAsync(source.Id);
        Assert.NotNull(stored);
        Assert.Equal(KnowledgeIndexStatus.Error.ToDbValue(), stored.IndexStatus);
        Assert.Empty(harness.Store.GetSourceChunks(KnowledgeHarness.TenantA, source.Id));
    }

    [Fact]
    public async Task Unknown_source_type_is_rejected_before_persisting()
    {
        using var harness = new KnowledgeHarness(KnowledgeHarness.TenantA);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Ingest.CreateAsync(new CreateKnowledgeSourceRequest("wiki", "ref", "Title")));

        Assert.Contains("document | site | faq", exception.Message, StringComparison.Ordinal);
        Assert.Empty(await harness.Sources.ListAsync());
    }
}
