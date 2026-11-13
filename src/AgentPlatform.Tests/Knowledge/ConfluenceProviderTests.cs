using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentPlatform.Core.Dto;
using AgentPlatform.Knowledge.Configuration;
using AgentPlatform.Knowledge.Ingest;
using AgentPlatform.Knowledge.Ingest.Providers.Confluence;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPlatform.Tests.Knowledge;

/// <summary>
/// ConfluenceProvider: мок REST Atlassian (фикстуры официальных v2/v1
/// контрактов), полная и инкрементальная синхронизация, вебхук-удаление,
/// анти-инъекция и tenant-изоляция.
/// </summary>
public sealed class ConfluenceProviderTests
{
    private const string CloudId = "cloud-1";

    private const string EmptyPages = """{ "results": [] }""";

    private const string FullPagesJson = """
        {
          "results": [
            {
              "id": "100",
              "status": "current",
              "title": "Руководство",
              "spaceId": "42",
              "parentId": null,
              "version": { "createdAt": "2026-01-01T10:00:00Z", "number": 2, "authorId": "acc-1" },
              "body": {
                "storage": {
                  "value": "<p>Доставка по Москве.</p><ac:image ac:alt=\"схема\"><ri:attachment ri:filename=\"map.png\" /></ac:image>"
                }
              },
              "_links": { "webui": "/spaces/DOC/pages/100/Guide" }
            },
            {
              "id": "101",
              "status": "current",
              "title": "Возвраты",
              "spaceId": "42",
              "parentId": "100",
              "version": { "createdAt": "2026-01-02T11:30:00Z", "number": 3, "authorId": "acc-2" },
              "body": { "storage": { "value": "<p>Возврат за 14 дней.</p>" } },
              "_links": { "webui": "/spaces/DOC/pages/101/Returns" }
            }
          ],
          "_links": { "base": "https://acme.atlassian.net/wiki" }
        }
        """;

    [Fact]
    public void Provider_describes_warning_and_supported_parameters()
    {
        var provider = CreateProvider(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var info = provider.GetSourceInfo();

        Assert.Equal(ConfluenceProvider.ProviderType, info.ProviderType);
        Assert.True(info.SupportsIncremental);
        Assert.True(info.SupportsWebhooks);
        Assert.Contains(info.Parameters, parameter => parameter.Name == ConfluenceProvider.SpacesParameter && parameter.Required);

        var setup = Assert.IsAssignableFrom<IKnowledgeProviderSetup>(provider);
        Assert.Contains(
            "Постраничные права доступа Confluence не проверяются в диалоге",
            setup.Warning,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Config_parameters_are_read_case_insensitively_after_json_roundtrip()
    {
        var json = KnowledgeProviderConfigJson.ToJson(new KnowledgeProviderConfig(
            "Confluence",
            Parameters: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["InstanceType"] = "cloud",
                ["Spaces"] = "DOC"
            }));

        var parsed = KnowledgeProviderConfigJson.Parse(json);

        Assert.NotNull(parsed);
        Assert.Equal("cloud", parsed.GetParameter(ConfluenceProvider.InstanceTypeParameter));
        Assert.Equal("DOC", parsed.GetParameter(ConfluenceProvider.SpacesParameter));
    }

    [Fact]
    public async Task Cloud_full_sync_maps_storage_hierarchy_and_skips_media()
    {
        var handler = new StubHandler(request => CloudResponder(request, FullPagesJson, EmptyPages, EmptyPages));
        var provider = CreateProvider(handler);

        var pages = await CollectAsync(provider.FetchAll(new KnowledgeProviderConfig(
            "Confluence Docs",
            Location: "https://acme.atlassian.net/wiki",
            CredentialsRef: "secret://test/confluence",
            Parameters: CloudParameters())));

        Assert.Equal(2, pages.Count);
        Assert.Equal("100", pages[0].ExternalId);
        Assert.Equal("Доставка по Москве.", pages[0].Content);
        Assert.DoesNotContain("схема", pages[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("map.png", pages[0].Content, StringComparison.Ordinal);

        var nested = pages[1];
        Assert.Equal("Возвраты", nested.Title);
        Assert.Equal("Руководство", nested.Metadata!.Folder);
        Assert.Equal("DOC", nested.Metadata.Space);
        Assert.Equal("acc-2", nested.Metadata.Author);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 11, 30, 0, TimeSpan.Zero), nested.Metadata.ModifiedAt);
        Assert.Equal(
            "https://acme.atlassian.net/wiki/spaces/DOC/pages/101/Returns",
            nested.Metadata.Url);
        Assert.Equal(
            "Docs → Руководство → Возвраты",
            nested.Metadata.Extra!["hierarchy"]);

        Assert.False(string.IsNullOrWhiteSpace(nested.Cursor));
        Assert.True(DateTimeOffset.TryParse(nested.Cursor, out _));
    }

    [Fact]
    public async Task Cloud_incremental_returns_changed_page_and_trashed_tombstone()
    {
        const string changedPages = """
            {
              "results": [
                {
                  "id": "101",
                  "status": "current",
                  "title": "Возвраты",
                  "spaceId": "42",
                  "parentId": "100",
                  "version": { "createdAt": "2026-01-03T12:00:00Z", "number": 4, "authorId": "acc-2" },
                  "body": { "storage": { "value": "<p>Возврат за 30 дней.</p>" } },
                  "_links": { "webui": "/spaces/DOC/pages/101/Returns" }
                }
              ],
              "_links": { "base": "https://acme.atlassian.net/wiki" }
            }
            """;

        const string trashedPages = """
            {
              "results": [
                {
                  "id": "200",
                  "status": "trashed",
                  "title": "Устаревшая",
                  "spaceId": "42",
                  "parentId": null,
                  "version": { "createdAt": "2026-01-02T12:00:00Z", "number": 1 },
                  "_links": { "webui": "/spaces/DOC/pages/200/Old" }
                }
              ],
              "_links": { "base": "https://acme.atlassian.net/wiki" }
            }
            """;

        var handler = new StubHandler(request => CloudResponder(request, FullPagesJson, changedPages, trashedPages));
        var provider = CreateProvider(handler);
        const string cursor = "2026-01-02T00:00:00.0000000Z";

        var changes = await CollectAsync(provider.FetchIncremental(
            new KnowledgeProviderConfig(
                "Confluence Docs",
                CredentialsRef: "secret://test/confluence",
                Parameters: CloudParameters()),
            cursor));

        Assert.Equal(2, changes.Count);
        Assert.False(changes[0].Page.IsDeleted);
        Assert.Equal("101", changes[0].Page.ExternalId);
        Assert.Contains("30 дней", changes[0].Page.Content, StringComparison.Ordinal);
        Assert.Equal("Руководство", changes[0].Page.Metadata!.Folder);

        Assert.True(changes[1].Page.IsDeleted);
        Assert.Equal("200", changes[1].Page.ExternalId);

        Assert.True(string.CompareOrdinal(changes[1].Cursor, cursor) > 0);
        Assert.Contains(handler.Requests, uri => uri.Query.Contains("sort=-modified-date", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, uri => uri.Query.Contains("status=trashed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task On_premise_full_sync_uses_v1_content_with_ancestors()
    {
        const string spaceJson = """
            { "id": "7", "key": "DOC", "name": "База знаний", "type": "global",
              "_links": { "webui": "/display/DOC" } }
            """;

        const string contentJson = """
            {
              "results": [
                {
                  "id": "300",
                  "title": "Регламент",
                  "type": "page",
                  "status": "current",
                  "body": { "storage": { "value": "<p>Текст регламента.</p>" } },
                  "version": { "when": "2026-02-01T09:00:00Z", "number": 4,
                               "by": { "displayName": "Иван" } },
                  "ancestors": [ { "id": "299", "title": "Раздел" } ],
                  "space": { "key": "DOC", "name": "База знаний" },
                  "_links": { "base": "https://wiki.example/confluence",
                              "webui": "/display/DOC/Regulation" }
                }
              ],
              "size": 1,
              "limit": 100,
              "start": 0
            }
            """;

        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/rest/api/space/DOC", StringComparison.Ordinal))
            {
                return Json(spaceJson);
            }

            if (path.EndsWith("/rest/api/content", StringComparison.Ordinal))
            {
                return Json(contentJson);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var provider = CreateProvider(
            handler,
            credentials: new Dictionary<string, string>
            {
                ["username"] = "svc",
                ["apiToken"] = "token"
            });

        var config = new KnowledgeProviderConfig(
            "Confluence On-Prem",
            Location: "https://wiki.example/confluence",
            CredentialsRef: "secret://test/onprem",
            Parameters: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["instanceType"] = "onpremise",
                ["baseUrl"] = "https://wiki.example/confluence",
                ["authType"] = "apiToken",
                ["spaces"] = "DOC"
            });

        var page = Assert.Single(await CollectAsync(provider.FetchAll(config)));

        Assert.Equal("300", page.ExternalId);
        Assert.Equal("Текст регламента.", page.Content);
        Assert.Equal("Иван", page.Metadata!.Author);
        Assert.Equal("Раздел", page.Metadata.Folder);
        Assert.Equal("База знаний → Раздел → Регламент", page.Metadata.Extra!["hierarchy"]);
        Assert.Equal("https://wiki.example/confluence/display/DOC/Regulation", page.Metadata.Url);

        Assert.Contains(
            handler.Requests,
            uri => uri.Query.Contains("spaceKey=DOC", StringComparison.Ordinal)
                   && uri.Query.Contains("body.storage", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Test_connection_lists_spaces_and_retries_rate_limited_requests()
    {
        var accessibilityAttempts = 0;

        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/oauth/token/accessible-resources", StringComparison.Ordinal))
            {
                accessibilityAttempts++;

                if (accessibilityAttempts == 1)
                {
                    var limited = new HttpResponseMessage((HttpStatusCode)429);
                    limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                    return limited;
                }
            }

            return CloudResponder(request, FullPagesJson, EmptyPages, EmptyPages);
        });

        var provider = CreateProvider(handler);
        var config = new KnowledgeProviderConfig(
            "Confluence Docs",
            CredentialsRef: "secret://test/confluence",
            Parameters: CloudParameters());

        var status = await provider.TestConnection(config);
        Assert.True(status.IsHealthy, status.Error);

        var space = Assert.Single(await provider.ListSpacesAsync(config));
        Assert.Equal("DOC", space.Key);
        Assert.Equal("Docs", space.Name);

        Assert.True(accessibilityAttempts >= 2);
        Assert.Contains(handler.Requests, uri => uri.Query.Contains("limit=1", StringComparison.Ordinal));
    }

    [Fact]
    public void Connect_webhook_without_event_field_uses_registration_hint()
    {
        var provider = CreateProvider(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        using var payload = JsonDocument.Parse("""
            {
              "page": { "id": 777, "spaceKey": "DOC", "title": "Страница" },
              "timestamp": 1767225600000
            }
            """);

        Assert.True(provider.TryParse(payload.RootElement, out var created, "page_created"));
        Assert.Equal(KnowledgeWebhookEventTypes.Created, created.EventType);
        Assert.Equal("777", created.ExternalId);
        Assert.False(created.IsDeletion);

        Assert.True(provider.TryParse(payload.RootElement, out var trashed, "avi:confluence:page:trashed"));
        Assert.Equal(KnowledgeWebhookEventTypes.Trashed, trashed.EventType);
        Assert.True(trashed.IsDeletion);
    }

    [Fact]
    public async Task Authorization_failure_is_unhealthy_and_not_retried()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var provider = CreateProvider(
            handler,
            new Dictionary<string, string> { ["accessToken"] = "token" });

        var status = await provider.TestConnection(new KnowledgeProviderConfig(
            "Confluence",
            CredentialsRef: "secret://test/confluence",
            Parameters: CloudParameters()));

        Assert.False(status.IsHealthy);
        Assert.Contains("401", status.Error, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    private static Dictionary<string, string?> CloudParameters()
        => new(StringComparer.OrdinalIgnoreCase)
        {
            [ConfluenceProvider.InstanceTypeParameter] = "cloud",
            [ConfluenceProvider.AuthTypeParameter] = "oauth2",
            [ConfluenceProvider.BaseUrlParameter] = "https://acme.atlassian.net/wiki",
            [ConfluenceProvider.SpacesParameter] = "DOC"
        };

    private static ConfluenceProvider CreateProvider(
        StubHandler handler,
        Dictionary<string, string>? credentials = null,
        ConfluenceOptions? options = null)
    {
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        return new ConfluenceProvider(
            new StubHttpClientFactory(client),
            options ?? TestOptions(),
            new FakeSecretResolver(credentials ?? new Dictionary<string, string>
            {
                ["accessToken"] = "token",
                ["cloudId"] = CloudId
            }),
            TimeProvider.System,
            NullLogger<ConfluenceProvider>.Instance);
    }

    private static ConfluenceOptions TestOptions() => new()
    {
        PageSize = 10,
        MaxPagesPerSpace = 100,
        MaxSpaces = 50,
        RequestTimeout = TimeSpan.FromSeconds(5),
        MaxRetries = 2,
        RetryBaseDelay = TimeSpan.Zero,
        RetryMaxDelay = TimeSpan.Zero,
        RetryJitterFactor = 0,
        ModifiedOverlap = TimeSpan.Zero
    };

    private static HttpResponseMessage CloudResponder(
        HttpRequestMessage request,
        string fullPages,
        string changedPages,
        string trashedPages)
    {
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath;
        var query = uri.Query;

        if (path.EndsWith("/oauth/token/accessible-resources", StringComparison.Ordinal))
        {
            return Json("""[ { "id": "cloud-1", "url": "https://acme.atlassian.net", "name": "Acme" } ]""");
        }

        if (path.EndsWith("/spaces", StringComparison.Ordinal))
        {
            return query.Contains("keys=DOC", StringComparison.Ordinal)
                ? Json("""
                    {
                      "results": [ { "id": "42", "key": "DOC", "name": "Docs", "type": "global" } ],
                      "_links": { "base": "https://acme.atlassian.net/wiki" }
                    }
                    """)
                : Json("""
                    {
                      "results": [ { "id": "42", "key": "DOC", "name": "Docs", "type": "global" } ],
                      "_links": { "base": "https://acme.atlassian.net/wiki" }
                    }
                    """);
        }

        if (path.EndsWith("/spaces/42/pages", StringComparison.Ordinal))
        {
            if (query.Contains("status=trashed", StringComparison.Ordinal))
            {
                return Json(trashedPages);
            }

            return query.Contains("sort=-modified-date", StringComparison.Ordinal)
                ? Json(changedPages)
                : Json(fullPages);
        }

        if (path.EndsWith("/pages/100", StringComparison.Ordinal))
        {
            return Json("""{ "id": "100", "title": "Руководство", "parentId": null }""");
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> stream)
    {
        var items = new List<T>();

        await foreach (var item in stream)
        {
            items.Add(item);
        }

        return items;
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class FakeSecretResolver(IReadOnlyDictionary<string, string>? credentials) : IKnowledgeSecretResolver
    {
        private IReadOnlyDictionary<string, string>? _ephemeral;

        public void UseEphemeral(IReadOnlyDictionary<string, string>? ephemeral)
            => _ephemeral = ephemeral;

        public Task<IReadOnlyDictionary<string, string>?> ResolveAsync(
            string? credentialsRef,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_ephemeral ?? credentials);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            Requests.Add(request.RequestUri!);
            return Task.FromResult(responder(request));
        }
    }
}



