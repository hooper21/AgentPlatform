using System.Text.RegularExpressions;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Dto;
using AgentPlatform.Infrastructure.Persistence.Entities;
using AgentPlatform.Infrastructure.Repositories;
using AgentPlatform.Knowledge.Configuration;
using AgentPlatform.Knowledge.Embeddings;
using AgentPlatform.Knowledge.Ingest;
using AgentPlatform.Knowledge.Ingest.Providers;
using AgentPlatform.Knowledge.Misses;
using AgentPlatform.Knowledge.Search;
using AgentPlatform.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPlatform.Tests.Knowledge;

/// <summary>
/// Детерминированный эмбеддер для тестов: bag-of-words хеширование в
/// <c>dimensions</c> измерений. Размерность настраивается (проверка mismatch).
/// </summary>
internal sealed class TestKnowledgeEmbedder(int dimensions = 768) : IKnowledgeEmbedder
{
    public int Dimensions => dimensions;

    public Task<ReadOnlyMemory<float>> EmbedAsync(
        Guid tenantId,
        string text,
        CancellationToken cancellationToken = default)
        => Task.FromResult<ReadOnlyMemory<float>>(Vectorize(text, dimensions));

    public static float[] Vectorize(string text, int dimensions)
    {
        var vector = new float[dimensions];

        foreach (Match match in Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]+"))
        {
            var bucket = (int)((uint)StringComparer.Ordinal.GetHashCode(match.Value) % (uint)dimensions);
            vector[bucket] += 1;
        }

        return vector;
    }
}

/// <summary>
/// Стенд Knowledge Service на InMemory EF + in-memory pgvector-заменителе:
/// реальные TenantScopedRepository, инжест, чанкер, эмбеддер-двойник и поиск.
/// </summary>
internal sealed class KnowledgeHarness : IDisposable
{
    public static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly TestDatabase _database;

    public KnowledgeHarness(
        Guid tenantId,
        IKnowledgeEmbedder? embedder = null,
        KnowledgeOptions? options = null,
        params IKnowledgeProvider[] providers)
    {
        _database = TestDatabase.Create();
        Options = options ?? new KnowledgeOptions();
        TenantContext = FakeTenantContext.For(tenantId);
        Sources = new TenantScopedRepository<KnowledgeSource>(_database.Context, TenantContext);
        Store = new InMemoryKnowledgeVectorStore();
        Misses = new InMemoryRagMissStore();
        Embedder = embedder ?? new TestKnowledgeEmbedder(Options.EmbeddingDimensions);

        var providerList = providers.Length > 0
            ? providers.ToList()
            : [new FaqKnowledgeProvider(), new DocumentKnowledgeProvider(Options)];

        Ingest = new KnowledgeIngestService(
            TenantContext,
            Sources,
            providerList,
            Embedder,
            Store,
            Options,
            TimeProvider.System,
            NullLogger<KnowledgeIngestService>.Instance);

        Search = new KnowledgeSearchService(
            Embedder,
            Store,
            Misses,
            Options,
            TimeProvider.System,
            NullLogger<KnowledgeSearchService>.Instance);
    }

    public KnowledgeOptions Options { get; }

    public FakeTenantContext TenantContext { get; }

    public ITenantScopedRepository<KnowledgeSource> Sources { get; }

    public InMemoryKnowledgeVectorStore Store { get; }

    public InMemoryRagMissStore Misses { get; }

    public IKnowledgeEmbedder Embedder { get; }

    public KnowledgeIngestService Ingest { get; }

    public KnowledgeSearchService Search { get; }

    public void SwitchTenant(Guid tenantId) => TenantContext.TenantId = tenantId;

    public void Dispose() => _database.Dispose();
}
