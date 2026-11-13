using System.Net;
using System.Text;
using AgentPlatform.Core.Dto;
using AgentPlatform.Runtime.Knowledge;

namespace AgentPlatform.Tests.Knowledge;

/// <summary>
/// HTTP-адаптер RAG-порта: GET /api/knowledge/search с tenant-заголовком,
/// пустой массив — пустой контекст, ошибка — исключение (Runtime: fail-open).
/// </summary>
public sealed class HttpKnowledgeContextProviderTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Search_request_carries_tenant_header_and_parses_hits()
    {
        var handler = new CapturingHandler(
            HttpStatusCode.OK,
            """
            [{"sourceId":"22222222-2222-2222-2222-222222222222","chunkId":7,
              "content":"Возврат — 14 дней.","score":0.91,
              "title":"Политика","sourceType":"document"}]
            """);

        var provider = new HttpKnowledgeContextProvider(
            new HttpClient(handler) { BaseAddress = new Uri("http://knowledge:8080") },
            topK: 3,
            threshold: 0.8);

        var context = await provider.GetContextAsync(TenantId, "условия возврата");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Contains("/api/knowledge/search", request.Url, StringComparison.Ordinal);
        Assert.Contains("topK=3", request.Url, StringComparison.Ordinal);
        Assert.Contains("threshold=0.8", request.Url, StringComparison.Ordinal);
        Assert.Equal(TenantId.ToString(), request.TenantHeader);

        var hit = Assert.Single(context.Hits);
        Assert.Equal("Политика", hit.Title);
        Assert.Equal("document", hit.SourceType);
        Assert.Equal(0.91, hit.Score, precision: 2);
    }

    [Fact]
    public async Task Empty_search_response_is_empty_context()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "[]");
        var provider = new HttpKnowledgeContextProvider(
            new HttpClient(handler) { BaseAddress = new Uri("http://knowledge:8080") });

        var context = await provider.GetContextAsync(TenantId, "неизвестный вопрос");

        Assert.True(context.IsEmpty);
    }

    [Fact]
    public async Task Unavailable_service_throws_for_runtime_fail_open()
    {
        var handler = new CapturingHandler(HttpStatusCode.ServiceUnavailable, "{}");
        var provider = new HttpKnowledgeContextProvider(
            new HttpClient(handler) { BaseAddress = new Uri("http://knowledge:8080") });

        await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.GetContextAsync(TenantId, "вопрос"));
    }

    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<(string Method, string Url, string? TenantHeader)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            Requests.Add((
                request.Method.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Headers.TryGetValues("X-Tenant-Id", out var values)
                    ? values.SingleOrDefault()
                    : null));

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
