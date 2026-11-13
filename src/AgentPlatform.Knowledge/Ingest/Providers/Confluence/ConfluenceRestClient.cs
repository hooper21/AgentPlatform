using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AgentPlatform.Knowledge.Ingest.Providers.Confluence;

/// <summary>Схема аутентификации Confluence (значение заголовка Authorization).</summary>
internal sealed record ConfluenceAuth(string Scheme, string Parameter)
{
    /// <summary>Bearer ACCESS_TOKEN (OAuth 2.0 Cloud / Personal Access Token Server).</summary>
    public static ConfluenceAuth Bearer(string token) => new("Bearer", token);

    /// <summary>Basic base64(username:apiToken) — API-токен/логин on-premise.</summary>
    public static ConfluenceAuth Basic(string username, string token)
        => new(
            "Basic",
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{username}:{token}")));

    public void Apply(HttpRequestMessage request)
        => request.Headers.Authorization = new AuthenticationHeaderValue(Scheme, Parameter);
}

/// <summary>Ошибка REST API Confluence (статус + безопасное сообщение без токена).</summary>
public sealed class ConfluenceApiException(int statusCode, string message) : Exception(message)
{
    /// <summary>HTTP-статус ответа.</summary>
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Минимальный REST-клиент Confluence: последовательная очередь запросов
/// (SemaphoreSlim), таймаут на запрос, повторы при 429/5xx с учётом Retry-After
/// и экспоненциальным backoff с jitter. Значения токенов не логируются.
/// </summary>
internal sealed class ConfluenceRestClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly ConfluenceOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly ConfluenceAuth _auth;
    private readonly SemaphoreSlim _queue = new(1, 1);

    public ConfluenceRestClient(
        HttpClient http,
        ConfluenceOptions options,
        TimeProvider timeProvider,
        ILogger logger,
        ConfluenceAuth auth)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
    }

    /// <summary>GET JSON с очередью и повторами. Документ освобождает вызывающий.</summary>
    public async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var payload = await SendAsync(uri, cancellationToken).ConfigureAwait(false);

        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            throw new ConfluenceApiException(
                502,
                $"Confluence вернул некорректный JSON для {uri.GetLeftPart(UriPartial.Path)}: {exception.Message}");
        }
    }

    /// <summary>Десериализует уже прочитанный JSON в типизированный контракт.</summary>
    public static T? Deserialize<T>(JsonDocument document)
        => JsonSerializer.Deserialize<T>(document.RootElement.GetRawText(), JsonOptions);

    public void Dispose() => _queue.Dispose();

    /// <summary>Строит абсолютный URL следующей страницы (относительный _links.next).</summary>
    public static Uri? NextUri(Uri current, string? next)
    {
        if (string.IsNullOrWhiteSpace(next))
        {
            return null;
        }

        if (!Uri.TryCreate(current, next, out var absolute))
        {
            return null;
        }

        // Пагинация не должна уводить запрос на чужой хост или понижать схему (SSRF).
        if (!string.Equals(absolute.Authority, current.Authority, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(absolute.Scheme, current.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConfluenceApiException(
                502,
                $"Confluence вернул некорректную ссылку пагинации: {absolute.Authority}.");
        }

        return absolute;
    }

    private async Task<string> SendAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await _queue.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.RequestTimeout);

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                _auth.Apply(request);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.UserAgent.ParseAdd(_options.UserAgent);

                HttpResponseMessage response;

                try
                {
                    response = await _http
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Запрос к Confluence не выполнен за {_options.RequestTimeout.TotalSeconds:0} с " +
                        $"({uri.GetLeftPart(UriPartial.Path)}).");
                }

                using (response)
                {
                    LogRateLimitHeaders(uri, response);

                    if (response.IsSuccessStatusCode)
                    {
                        return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                    }

                    var status = (int)response.StatusCode;
                    var retryable = status == 429 || status >= 500;

                    if (retryable && attempt < _options.MaxRetries)
                    {
                        var delay = ResolveRetryDelay(response, attempt);
                        _logger.LogWarning(
                            "Confluence {Status} на {Url}: повтор {Attempt}/{MaxRetries} через {Delay} мс",
                            status,
                            uri.GetLeftPart(UriPartial.Path),
                            attempt + 1,
                            _options.MaxRetries,
                            (int)delay.TotalMilliseconds);

                        await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    throw new ConfluenceApiException(
                        status,
                        status switch
                        {
                            401 => "Confluence отклонил токен (401): проверьте учётные данные.",
                            403 => "Токену Confluence недостаточно прав (403).",
                            404 => "Ресурс Confluence не найден (404).",
                            429 => "Confluence ограничил частоту запросов (429): повторы исчерпаны.",
                            _ => $"Confluence вернул HTTP {status} для {uri.GetLeftPart(UriPartial.Path)}."
                        });
                }
            }
            finally
            {
                _queue.Release();
            }
        }
    }

    private TimeSpan ResolveRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = ReadRetryAfter(response);
        if (retryAfter is { } value && value > TimeSpan.Zero)
        {
            return value > _options.RetryMaxDelay ? _options.RetryMaxDelay : value;
        }

        var exponent = Math.Min(attempt, 16);
        var baseDelay = _options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var capped = Math.Min(baseDelay, _options.RetryMaxDelay.TotalMilliseconds);

        if (_options.RetryJitterFactor > 0 && capped > 0)
        {
            var jitter = capped * _options.RetryJitterFactor * Random.Shared.NextDouble();
            capped = Math.Min(capped + jitter, _options.RetryMaxDelay.TotalMilliseconds);
        }

        return TimeSpan.FromMilliseconds(capped);
    }

    private TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var delay = date - _timeProvider.GetUtcNow();
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }

    private void LogRateLimitHeaders(Uri uri, HttpResponseMessage response)
    {
        var remaining = ReadHeader(response, "X-RateLimit-Remaining");
        var nearLimit = ReadHeader(response, "X-RateLimit-NearLimit");

        if (remaining is null && nearLimit is null)
        {
            return;
        }

        _logger.LogDebug(
            "Confluence rate limit: remaining={Remaining}, nearLimit={NearLimit} ({Url})",
            remaining,
            nearLimit,
            uri.GetLeftPart(UriPartial.Path));
    }

    private static string? ReadHeader(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
