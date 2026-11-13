using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Dto;
using AgentPlatform.Knowledge.Configuration;
using AgentPlatform.Knowledge.Contracts;
using AgentPlatform.Knowledge.Ingest;
using AgentPlatform.Knowledge.Ingest.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPlatform.Tests.Knowledge;

/// <summary>
/// Контракт IKnowledgeProvider: реестр встроенных провайдеров, round-trip для
/// document/site/faq, движение курсора инкремента, удаление страниц и
/// tenant-изоляция синхронизации.
/// </summary>
public sealed class KnowledgeProviderContractTests
{
    [Fact]
    public void Builtin_providers_are_described_by_registry()
    {
        var options = new KnowledgeOptions();
        var registry = new KnowledgeProviderRegistry(
        [
            new DocumentProvider(options, TimeProvider.System),
            new SiteProvider(
                new StaticHttpClientFactory(new HttpClient(new StaticHandler())),
                options,
                TimeProvider.System,
                NullLogger<SiteProvider>.Instance),
            new FaqProvider(TimeProvider.System)
        ]);

        var infos = registry.Describe();

        Assert.Equal(
            [KnowledgeProviderTypes.Document, KnowledgeProviderTypes.Site, KnowledgeProviderTypes.Faq],
            infos.Select(info => info.ProviderType));

        Assert.Equal(KnowledgeConnectionTypes.File, infos[0].ConnectionType);
        Assert.True(infos[0].SupportsIncremental);
        Assert.Contains(infos[0].Parameters, parameter => parameter.Name == "location" && parameter.Required);

        Assert.Equal(KnowledgeConnectionTypes.Http, infos[1].ConnectionType);
        Assert.True(infos[1].SupportsWebhooks);

        Assert.Equal(KnowledgeConnectionTypes.Manual, infos[2].ConnectionType);
        Assert.Contains(infos[2].Parameters, parameter => parameter.Name == "content" && parameter.Required);
    }

    [Fact]
    public async Task Document_provider_round_trip_and_incremental_cursor_moves()
    {
        var root = Directory.CreateTempSubdirectory("knowledge-contract-doc-");
        try
        {
            var path = Path.Combine(root.FullName, "policy.md");
            await File.WriteAllTextAsync(path, "# Политика\nВозврат за 14 дней.\n");

            var provider = new DocumentProvider(
                new KnowledgeOptions { DocumentRoot = root.FullName },
                TimeProvider.System);
            var config = new KnowledgeProviderConfig("Политика возврата", Location: "policy.md");

            var status = await provider.TestConnection(config);
            Assert.True(status.IsHealthy, status.Message);

            var pages = await CollectAsync(provider.FetchAll(config));
            var page = Assert.Single(pages);
            Assert.Equal("Политика возврата", page.Title);
            Assert.Equal("#0", page.ExternalId[^2..]);
            Assert.NotNull(page.Metadata?.ModifiedAt);
            Assert.False(string.IsNullOrWhiteSpace(page.Cursor));

            // Контент не менялся — инкремент пуст, курсор остаётся прежним.
            var unchanged = await CollectAsync(provider.FetchIncremental(config, page.Cursor));
            Assert.Empty(unchanged);

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            var changes = await CollectAsync(provider.FetchIncremental(config, page.Cursor));

            var change = Assert.Single(changes);
            Assert.NotEqual(page.Cursor, change.Cursor);
            Assert.Contains("14 дней", change.Page.Content, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Faq_provider_round_trip_and_incremental_cursor_moves()
    {
        var provider = new FaqProvider(TimeProvider.System);
        var first = new KnowledgeProviderConfig(
            "Возврат товара",
            Content: "Вернуть товар можно в течение 14 дней.");

        var status = await provider.TestConnection(first);
        Assert.True(status.IsHealthy, status.Message);

        var page = Assert.Single(await CollectAsync(provider.FetchAll(first)));
        Assert.Equal(FaqProvider.PageExternalId, page.ExternalId);
        Assert.False(string.IsNullOrWhiteSpace(page.Cursor));

        var unchanged = await CollectAsync(provider.FetchIncremental(first, page.Cursor));
        Assert.Empty(unchanged);

        var second = first with { Content = "Вернуть товар можно в течение 30 дней." };
        var change = Assert.Single(await CollectAsync(provider.FetchIncremental(second, page.Cursor)));

        Assert.NotEqual(page.Cursor, change.Cursor);
        Assert.Contains("30 дней", change.Page.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Document_source_round_trip_through_pipeline()
    {
        var root = Directory.CreateTempSubdirectory("knowledge-contract-pipe-");
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root.FullName, "policy.md"),
                "# Политика возврата\nВозврат за 14 дней.\n");

            var options = new KnowledgeOptions { DocumentRoot = root.FullName };
            using var harness = new KnowledgeHarness(
                KnowledgeHarness.TenantA,
                options: options,
                providers: [new DocumentProvider(options, TimeProvider.System)]);

            var source = await harness.Ingest.CreateAsync(new CreateKnowledgeSourceRequest(
                "document",
                Ref: "policy.md",
                Title: "Политика возврата"));

            Assert.Equal(KnowledgeProviderTypes.Document, source.ProviderType);
            Assert.Equal("ready", source.IndexStatus);
            Assert.True(source.ChunkCount > 0);
            Assert.False(string.IsNullOrWhiteSpace(source.LastCursor));

            var chunks = harness.Store.GetSourceChunks(KnowledgeHarness.TenantA, source.Id);
            Assert.Contains("\"external_id\"", chunks[0].Metadata, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Site_source_round_trip_through_pipeline()
    {
        var options = new KnowledgeOptions
        {
            SiteMaxPages = 5,
            SiteRequestTimeout = TimeSpan.FromSeconds(5)
        };

        var handler = new StaticHandler(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://site.example/robots.txt"] = "User-agent: *\nAllow: /\n",
            ["https://site.example/"] = "<html><head><title>Сайт</title></head><body>Доставка по Москве.</body></html>"
        });

        var provider = new SiteProvider(
            new StaticHttpClientFactory(new HttpClient(handler)),
            options,
            TimeProvider.System,
            NullLogger<SiteProvider>.Instance);

        using var harness = new KnowledgeHarness(
            KnowledgeHarness.TenantA,
            options: options,
            providers: [provider]);

        var source = await harness.Ingest.CreateAsync(new CreateKnowledgeSourceRequest(
            "site",
            Ref: "https://site.example/",
            Title: "Сайт"));

        Assert.Equal(KnowledgeProviderTypes.Site, source.ProviderType);
        Assert.Equal("ready", source.IndexStatus);
        Assert.True(source.ChunkCount > 0);
        Assert.False(string.IsNullOrWhiteSpace(source.LastCursor));
    }

    [Fact]
    public async Task Incremental_sync_moves_cursor_and_replaces_page_chunks()
    {
        using var harness = new KnowledgeHarness(KnowledgeHarness.TenantA);
        var source = await harness.Ingest.CreateAsync(new CreateKnowledgeSourceRequest(
            "faq",
            Ref: null,
            Title: "Доставка",
            Content: "Доставка курьером — один день."));

        var initialCursor = source.LastCursor;
        Assert.False(string.IsNullOrWhiteSpace(initialCursor));

        // Контент источника изменился: инкремент двигает курсор и заменяет
        // чанки только изменённой страницы.
        source.Config = KnowledgeProviderConfigJson.ToJson(new KnowledgeProviderConfig(
            "Доставка",
            Content: "Доставка курьером — два дня."));
        harness.Sources.Update(source);
        await harness.Sources.SaveChangesAsync();

        var synced = await harness.Ingest.SyncIncrementalAsync(source.Id);
        Assert.NotNull(synced);
        Assert.NotEqual(initialCursor, synced.LastCursor);

        var chunks = harness.Store.GetSourceChunks(KnowledgeHarness.TenantA, source.Id);
        var chunk = Assert.Single(chunks);
        Assert.Contains("два дня", chunk.Content, StringComparison.Ordinal);
        Assert.Equal(FaqProvider.PageExternalId, chunk.ExternalId);

        // Повторный инкремент без изменений курсор не двигает.
        var repeated = await harness.Ingest.SyncIncrementalAsync(source.Id);
        Assert.Equal(synced.LastCursor, repeated!.LastCursor);
    }


    [Fact]
    public async Task Deleted_page_removes_chunks_by_external_id()
    {
        var provider = new TestProvider();
        using var harness = new KnowledgeHarness(
            KnowledgeHarness.TenantA,
            providers: [provider]);

        var source = await harness.Ingest.CreateAsync(new CreateKnowledgeSourceRequest(
            "test",
            Ref: "external-source",
            Title: "Тестовый источник"));

        Assert.NotEmpty(harness.Store.GetSourceChunks(KnowledgeHarness.TenantA, source.Id));

        provider.Incremental.Add(new KnowledgeChange(
            new KnowledgePage("page-1", "Тест", Content: string.Empty, IsDeleted: true),
            Cursor: "cursor-2"));

        var synced = await harness.Ingest.SyncIncrementalAsync(source.Id);

        Assert.NotNull(synced);
        Assert.Equal("cursor-2", synced.LastCursor);
        Assert.Empty(harness.Store.GetSourceChunks(KnowledgeHarness.TenantA, source.Id));
    }

    [Fact]
    public async Task Incremental_sync_is_tenant_scoped()
    {
        using var harness = new KnowledgeHarness(KnowledgeHarness.TenantA);
        var sourceA = await harness.Ingest.CreateAsync(new CreateKnowledgeSourceRequest(
            "faq",
            Ref: null,
            Title: "Тенант A",
            Content: "Внутренние данные тенанта A."));

        var cursorA = sourceA.LastCursor;

        harness.SwitchTenant(KnowledgeHarness.TenantB);

        Assert.Null(await harness.Ingest.SyncIncrementalAsync(sourceA.Id));
        Assert.Empty(harness.Store.GetSourceChunks(KnowledgeHarness.TenantB, sourceA.Id));
        Assert.NotEmpty(harness.Store.GetSourceChunks(KnowledgeHarness.TenantA, sourceA.Id));
        Assert.Equal(cursorA, sourceA.LastCursor);
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> stream)
    {
        var items = new List<T>();

        await foreach (var item in stream)
        {
            items.Add(item);
        }

        return items;
    }

    private sealed class TestProvider : IKnowledgeProvider
    {
        public List<KnowledgeChange> Incremental { get; } = [];

        public KnowledgeProviderInfo GetSourceInfo() => new(
            "test",
            "Тестовый провайдер",
            KnowledgeConnectionTypes.Manual,
            "Провайдер для проверки инкремента и удаления страниц.",
            [],
            SupportsIncremental: true,
            SupportsWebhooks: true);

        public Task<KnowledgeConnectionStatus> TestConnection(
            KnowledgeProviderConfig config,
            CancellationToken cancellationToken = default)
            => Task.FromResult(KnowledgeConnectionStatus.Healthy(
                "ok",
                DateTimeOffset.UnixEpoch));

        public async IAsyncEnumerable<KnowledgePage> FetchAll(
            KnowledgeProviderConfig config,
            DateTimeOffset? since = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            yield return new KnowledgePage(
                "page-1",
                config.Title,
                "Тестовый контент для инжеста.",
                Cursor: "cursor-1");
        }

        public async IAsyncEnumerable<KnowledgeChange> FetchIncremental(
            KnowledgeProviderConfig config,
            string? cursor = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);

            foreach (var change in Incremental)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return change;
            }
        }
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticHandler(IReadOnlyDictionary<string, string>? pages = null) : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, string> _pages =
            pages ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://site.example/robots.txt"] = "User-agent: *\nAllow: /\n",
                ["https://site.example/"] = "<html><body>Доставка по Москве.</body></html>"
            };

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;

            if (!_pages.TryGetValue(uri, out var content))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");

            return Task.FromResult(response);
        }
    }
}

