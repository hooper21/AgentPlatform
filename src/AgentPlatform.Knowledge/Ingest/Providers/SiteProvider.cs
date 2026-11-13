using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Dto;
using AgentPlatform.Knowledge.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentPlatform.Knowledge.Ingest.Providers;

/// <summary>
/// Провайдер site: обход одного уровня от указанного URL. Соблюдает robots.txt
/// (правила для User-Agent платформы), таймаут каждого запроса — 5 с
/// (KnowledgeOptions.SiteRequestTimeout), количество страниц ограничено.
/// Курсор — время начала обхода (ISO 8601 UTC); инкремент фильтрует страницы
/// по HTTP Last-Modified, если сервер его отдаёт.
/// </summary>
public sealed class SiteProvider(
    IHttpClientFactory httpClientFactory,
    KnowledgeOptions options,
    TimeProvider timeProvider,
    ILogger<SiteProvider> logger) : IKnowledgeProvider
{
    /// <summary>Имя HttpClient в DI (таймаут задаётся при регистрации).</summary>
    public const string HttpClientName = "knowledge-site";

    /// <summary>Параметр config: максимум страниц обхода.</summary>
    public const string MaxPagesParameter = "maxPages";

    private readonly IHttpClientFactory _httpClientFactory =
        httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));

    private readonly KnowledgeOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<SiteProvider> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public KnowledgeProviderInfo GetSourceInfo() => new(
        KnowledgeProviderTypes.Site,
        "Сайт (HTTP)",
        KnowledgeConnectionTypes.Http,
        "Обход одного уровня от указанного URL с соблюдением robots.txt.",
        [
            new KnowledgeProviderParameter(
                KnowledgeProviderParameterNames.Location,
                "URL сайта",
                Required: true,
                Description: "Абсолютный http(s)-URL стартовой страницы."),
            new KnowledgeProviderParameter(
                MaxPagesParameter,
                "Максимум страниц",
                Description: "Ограничение обхода; по умолчанию — Knowledge:SiteMaxPages.",
                DefaultValue: _options.SiteMaxPages.ToString(CultureInfo.InvariantCulture))
        ],
        SupportsIncremental: true,
        SupportsWebhooks: true);

    /// <inheritdoc />
    public async Task<KnowledgeConnectionStatus> TestConnection(
        KnowledgeProviderConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var now = _timeProvider.GetUtcNow();

        if (!TryCreateStart(config.Location, out var start))
        {
            return KnowledgeConnectionStatus.Unhealthy(
                "Для источника type=site location должен быть абсолютным http(s)-URL.",
                now);
        }

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var robots = await LoadRobotsAsync(client, start, cancellationToken).ConfigureAwait(false);

            if (!robots.IsAllowed(start))
            {
                return KnowledgeConnectionStatus.Unhealthy(
                    $"robots.txt запрещает обход {start} для User-Agent '{_options.SiteUserAgent}'.",
                    now);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.SiteRequestTimeout);

            using var response = await client
                .GetAsync(start, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? KnowledgeConnectionStatus.Healthy(
                    $"GET {start} → {(int)response.StatusCode} {response.ReasonPhrase}.",
                    now)
                : KnowledgeConnectionStatus.Unhealthy(
                    $"GET {start} → {(int)response.StatusCode} {response.ReasonPhrase}.",
                    now);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return KnowledgeConnectionStatus.Unhealthy(
                $"GET {start} не завершился за {_options.SiteRequestTimeout.TotalSeconds:0} с.",
                now);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return KnowledgeConnectionStatus.Unhealthy(
                $"Не удалось подключиться к {start}.",
                now,
                exception.Message);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<KnowledgePage> FetchAll(
        KnowledgeProviderConfig config,
        DateTimeOffset? since = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!TryCreateStart(config.Location, out var start))
        {
            throw new ArgumentException(
                $"Для источника type=site location должен быть абсолютным http(s)-URL, получено '{config.Location}'.",
                nameof(config));
        }

        var maxPages = ResolveMaxPages(config);
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var robots = await LoadRobotsAsync(client, start, cancellationToken).ConfigureAwait(false);

        if (!robots.IsAllowed(start))
        {
            throw new InvalidOperationException(
                $"robots.txt запрещает обход {start} для User-Agent '{_options.SiteUserAgent}'.");
        }

        // Курсор фиксируется на начале обхода: изменения после него попадут
        // в следующий инкрементальный проход.
        var cursor = _timeProvider.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

        var queue = new Queue<Uri>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start.AbsoluteUri };
        queue.Enqueue(start);
        var position = 0;
        var fetchedPages = 0;
        var childrenExpanded = false;

        while (queue.Count > 0 && fetchedPages < maxPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = queue.Dequeue();

            string? html;
            DateTimeOffset? lastModified;
            try
            {
                (html, lastModified) = await FetchHtmlAsync(client, uri, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"GET {uri} не завершился за {_options.SiteRequestTimeout.TotalSeconds:0} с.");
            }
            catch (Exception exception) when (fetchedPages > 0)
            {
                // Ошибка дочерней страницы не отменяет инжест: остальные страницы индексируются.
                _logger.LogWarning(
                    exception,
                    "Knowledge site crawl skipped {Url}: {Error}",
                    uri,
                    exception.Message);
                continue;
            }

            fetchedPages++;

            if (html is null)
            {
                continue;
            }

            var title = HtmlText.ExtractTitle(html, uri.AbsoluteUri);
            var content = HtmlText.ToPlainText(html);

            if (content.Length > 0 && ShouldYield(since, lastModified))
            {
                yield return new KnowledgePage(
                    ExternalId: uri.AbsoluteUri,
                    Title: title,
                    Content: content,
                    Metadata: new KnowledgePageMetadata(
                        ModifiedAt: lastModified,
                        Url: uri.AbsoluteUri,
                        Space: start.Host,
                        Folder: FolderOf(uri)),
                    Heading: title,
                    Position: position++,
                    Cursor: cursor);
            }

            if (childrenExpanded)
            {
                continue;
            }

            childrenExpanded = true;

            foreach (var link in HtmlText.ExtractLinks(html, uri))
            {
                if (!string.Equals(link.Host, start.Host, StringComparison.OrdinalIgnoreCase)
                    || !robots.IsAllowed(link)
                    || !seen.Add(link.AbsoluteUri))
                {
                    continue;
                }

                queue.Enqueue(link);
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<KnowledgeChange> FetchIncremental(
        KnowledgeProviderConfig config,
        string? cursor = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var since = ParseCursor(cursor);

        await foreach (var page in FetchAll(config, since, cancellationToken).ConfigureAwait(false))
        {
            yield return new KnowledgeChange(page, page.Cursor);
        }
    }

    private int ResolveMaxPages(KnowledgeProviderConfig config)
    {
        var raw = config.GetParameter(MaxPagesParameter);

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxPages)
            && maxPages > 0
                ? maxPages
                : _options.SiteMaxPages;
    }

    private static bool ShouldYield(DateTimeOffset? since, DateTimeOffset? modifiedAt)
        => since is null || modifiedAt is null || modifiedAt > since;

    private static DateTimeOffset? ParseCursor(string? cursor)
        => DateTimeOffset.TryParse(
            cursor,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var value)
            ? value
            : null;

    private static string FolderOf(Uri uri)
    {
        var path = uri.AbsolutePath;
        var separator = path.LastIndexOf('/');

        return separator <= 0 ? "/" : path[..(separator + 1)];
    }

    private static bool TryCreateStart(string? raw, [NotNullWhen(true)] out Uri? start)
    {
        start = null;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        start = uri;
        return true;
    }

    private async Task<RobotsRules> LoadRobotsAsync(
        HttpClient client,
        Uri start,
        CancellationToken cancellationToken)
    {
        var robotsUri = new Uri(start.GetLeftPart(UriPartial.Authority) + "/robots.txt", UriKind.Absolute);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.SiteRequestTimeout);

            using var response = await client
                .GetAsync(robotsUri, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "robots.txt returned {StatusCode} for {Host}: crawl is allowed",
                    (int)response.StatusCode,
                    robotsUri.Host);
                return RobotsRules.AllowAll;
            }

            var content = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return RobotsRules.Parse(content, _options.SiteUserAgent);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"robots.txt {robotsUri} не получен за {_options.SiteRequestTimeout.TotalSeconds:0} с.");
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException(
                $"Не удалось проверить robots.txt {robotsUri}: обход запрещён до подтверждения правил.",
                exception);
        }
    }

    private async Task<(string? Html, DateTimeOffset? LastModified)> FetchHtmlAsync(
        HttpClient client,
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.SiteRequestTimeout);

        using var response = await client
            .GetAsync(uri, HttpCompletionOption.ResponseContentRead, timeout.Token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GET {uri} → {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Knowledge site crawl skipped non-HTML {Url} ({MediaType})", uri, mediaType);
            return (null, null);
        }

        var html = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return (html, response.Content.Headers.LastModified);
    }

}
