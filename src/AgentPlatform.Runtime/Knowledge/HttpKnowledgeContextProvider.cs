using System.Globalization;
using System.Net.Http.Json;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Json;

namespace AgentPlatform.Runtime.Knowledge;

/// <summary>
/// HTTP-адаптер порта RAG: GET /api/knowledge/search Knowledge Service с
/// заголовком X-Tenant-Id. Ошибки сервиса пробрасываются — Runtime трактует их
/// как пустой контекст (диалог не ломается).
/// </summary>
public sealed class HttpKnowledgeContextProvider(
    HttpClient httpClient,
    int topK = KnowledgeContext.DefaultTopK,
    double threshold = KnowledgeContext.DefaultThreshold) : IKnowledgeContextProvider
{
    /// <summary>Путь поиска в Knowledge Service.</summary>
    public const string SearchPath = "/api/knowledge/search";

    /// <summary>Заголовок тенанта (v1-заглушка аутентификации).</summary>
    public const string TenantHeader = "X-Tenant-Id";

    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    /// <inheritdoc />
    public async Task<KnowledgeContext> GetContextAsync(
        Guid tenantId,
        string query,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("tenant_id is required for RAG search", nameof(tenantId));
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return KnowledgeContext.Empty(query ?? string.Empty);
        }

        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"{SearchPath}?query={Uri.EscapeDataString(query)}&topK={topK}&threshold={threshold}");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation(TenantHeader, tenantId.ToString());

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Knowledge search failed: HTTP {(int)response.StatusCode}.",
                inner: null,
                statusCode: response.StatusCode);
        }

        var hits = await response.Content
            .ReadFromJsonAsync<KnowledgeHit[]>(PlatformJson.ApiOptions, cancellationToken)
            .ConfigureAwait(false);

        return new KnowledgeContext(query, hits ?? []);
    }
}
