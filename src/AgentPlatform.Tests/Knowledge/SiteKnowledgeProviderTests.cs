using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AgentPlatform.Knowledge.Configuration;
using AgentPlatform.Knowledge.Ingest.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPlatform.Tests.Knowledge;

/// <summary>
/// Site-инжест: обход одного уровня, robots.txt соблюдается, внешние хосты
/// не обходятся. HTTP подменяется fake handler (без сети).
/// </summary>
public sealed class SiteKnowledgeProviderTests
{
    [Fact]
    public async Task Crawl_respects_robots_and_visits_one_level_of_same_origin()
    {
        var handler = new FakeSiteHandler(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://shop.example/robots.txt"] = "User-agent: *\nDisallow: /private\n",
            ["https://shop.example/"] = """
                <html><head><title>Главная</title></head><body>
                <h1>Магазин</h1>
                <a href="/about">О нас</a>
                <a href="/private/secret">Секрет</a>
                <a href="https://other.example/page">Чужой сайт</a>
                </body></html>
                """,
            ["https://shop.example/about"] = """
                <html><head><title>О нас</title></head><body><p>Мы продаём товары.</p>
                <a href="/deep">Глубоко</a></body></html>
                """,
            ["https://shop.example/deep"] = "<html><body>второй уровень</body></html>",
            ["https://other.example/page"] = "<html><body>чужой</body></html>"
        });

        var provider = CreateProvider(handler);
        var pages = new List<AgentPlatform.Core.Dto.KnowledgePage>();

        await foreach (var page in provider.ReadAsync(
            new AgentPlatform.Core.Abstractions.KnowledgeProviderRequest("Магазин", "https://shop.example/")))
        {
            pages.Add(page);
        }

        Assert.Equal(2, pages.Count);
        Assert.Equal("Главная", pages[0].Title);
        Assert.Equal("О нас", pages[1].Title);
        Assert.DoesNotContain(pages, page => page.Content.Contains("второй уровень", StringComparison.Ordinal));
        Assert.DoesNotContain(pages, page => page.Content.Contains("чужой", StringComparison.Ordinal));
        Assert.DoesNotContain(
            handler.Requests,
            url => url.Contains("/private", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Robots_disallow_of_entry_url_fails_ingest()
    {
        var handler = new FakeSiteHandler(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://shop.example/robots.txt"] = "User-agent: *\nDisallow: /private\n",
            ["https://shop.example/private/page"] = "<html><body>secret</body></html>"
        });

        var provider = CreateProvider(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in provider.ReadAsync(
                new AgentPlatform.Core.Abstractions.KnowledgeProviderRequest(
                    "Private",
                    "https://shop.example/private/page")))
            {
            }
        });
    }

    [Fact]
    public async Task Request_timeout_is_enforced()
    {
        var options = new KnowledgeOptions
        {
            SiteRequestTimeout = TimeSpan.FromMilliseconds(50),
            SiteMaxPages = 10
        };

        var client = new HttpClient(new HangingHandler());
        var provider = new SiteKnowledgeProvider(
            new FakeHttpClientFactory(client),
            options,
            NullLogger<SiteKnowledgeProvider>.Instance);

        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var _ in provider.ReadAsync(
                new AgentPlatform.Core.Abstractions.KnowledgeProviderRequest(
                    "Медленный сайт",
                    "https://slow.example/")))
            {
            }
        });
    }

    private static SiteKnowledgeProvider CreateProvider(FakeSiteHandler handler)
    {
        var options = new KnowledgeOptions
        {
            SiteRequestTimeout = TimeSpan.FromSeconds(5),
            SiteMaxPages = 10
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        return new SiteKnowledgeProvider(
            new FakeHttpClientFactory(client),
            options,
            NullLogger<SiteKnowledgeProvider>.Instance);
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class FakeHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class FakeSiteHandler(IReadOnlyDictionary<string, string> pages) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            Requests.Add(uri);

            if (!pages.TryGetValue(uri, out var content))
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
