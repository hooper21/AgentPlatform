using System.Net.Http.Json;
using System.Text.Json;

namespace AgentPlatform.TenantApp.Web;

/// <summary>
/// Типизированный клиент Knowledge Service. Кабинет не работает с pgvector и
/// чанками напрямую: список провайдеров, создание источников и синхронизация —
/// только через API Knowledge Service. Тенант передаётся заголовком X-Tenant-Id
/// (v1-заглушка аутентификации Knowledge Service).
/// </summary>
public sealed class KnowledgeApiClient(HttpClient http)
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>Реестр провайдеров знаний (тип, название, параметры подключения).</summary>
    public Task<List<KnowledgeProviderDto>> GetProvidersAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
        => SendAsync<List<KnowledgeProviderDto>>(
            HttpMethod.Get,
            "api/knowledge/providers",
            tenantId,
            body: null,
            cancellationToken);

    /// <summary>Источники знаний тенанта.</summary>
    public Task<List<KnowledgeSourceDto>> GetSourcesAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
        => SendAsync<List<KnowledgeSourceDto>>(
            HttpMethod.Get,
            "api/knowledge/sources",
            tenantId,
            body: null,
            cancellationToken);

    /// <summary>Создаёт источник через провайдера из реестра (полная индексация).</summary>
    public Task<KnowledgeSourceDto> CreateSourceAsync(
        Guid tenantId,
        KnowledgeSourceCreateDto request,
        CancellationToken cancellationToken = default)
        => SendAsync<KnowledgeSourceDto>(
            HttpMethod.Post,
            "api/knowledge/sources",
            tenantId,
            request,
            cancellationToken);

    /// <summary>Инкрементальная синхронизация (по расписанию/вебхуку — тот же путь).</summary>
    public Task<KnowledgeSourceDto> SyncSourceAsync(
        Guid tenantId,
        Guid sourceId,
        CancellationToken cancellationToken = default)
        => SendAsync<KnowledgeSourceDto>(
            HttpMethod.Post,
            $"api/knowledge/sources/{sourceId}/sync",
            tenantId,
            body: null,
            cancellationToken);

    /// <summary>Полная переиндексация источника.</summary>
    public Task<KnowledgeSourceDto> ReindexSourceAsync(
        Guid tenantId,
        Guid sourceId,
        CancellationToken cancellationToken = default)
        => SendAsync<KnowledgeSourceDto>(
            HttpMethod.Post,
            $"api/knowledge/sources/{sourceId}/reindex",
            tenantId,
            body: null,
            cancellationToken);

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string url,
        Guid tenantId,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("X-Tenant-Id", tenantId.ToString());

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonSerializerOptions.Web);
        }

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new CabinetApiException(status, ExtractMessage(raw), raw);
        }

        var payload = await response.Content
            .ReadFromJsonAsync<T>(JsonSerializerOptions.Web, cancellationToken)
            .ConfigureAwait(false);

        return payload ?? throw new CabinetApiException(
            (int)response.StatusCode,
            "Knowledge API returned an empty response.");
    }

    private static string ExtractMessage(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "Knowledge API request failed.";
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString() ?? raw;
                }

                if (root.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString() ?? raw;
                }
            }
        }
        catch (JsonException)
        {
            // Не JSON — показываем как есть.
        }

        return raw;
    }
}
