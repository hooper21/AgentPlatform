using System.Runtime.CompilerServices;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Enums;
using AgentPlatform.Knowledge.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentPlatform.Knowledge.Ingest.Providers;

/// <summary>
/// Провайдер site: обход одного уровня от указанного URL. Соблюдает robots.txt
/// (правила для User-Agent платформы), таймаут каждого запроса — 5 с
/// (KnowledgeOptions.SiteRequestTimeout), количество страниц ограничено.
/// </summary>
public sealed class SiteKnowledgeProvider(
    IHttpClientFactory httpClientFactory,
    KnowledgeOptions options,
    ILogger<SiteKnowledgeProvider> logger) : IKnowledgeProvider
{
    /// <summary>Имя HttpClient в DI (таймаут задаётся при регистрации).</summary>
    public const string HttpClientName = "knowledge-site";

    private readonly IHttpClientFactory _httpClientFactory =
        httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));

    private readonly KnowledgeOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    private readonly ILogger<SiteKnowledgeProvider> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public KnowledgeSourceType SourceType => KnowledgeSourceType.Site;

    /// <inheritdoc />
    public async IAsyncEnumerable<KnowledgePage> ReadAsync(
        KnowledgeProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Uri.TryCreate(request.Ref, UriKind.Absolute, out var start)
            || (start.Scheme != Uri.UriSchemeHttp && start.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"Для источника type=site ref должен быть абсолютным http(s)-URL, получено '{request.Ref}'.",
                nameof(request));
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        var robots = await LoadRobotsAsync(client, start, cancellationToken).ConfigureAwait(false);

        if (!robots.IsAllowed(start))
        {
            throw new InvalidOperationException(
                $"robots.txt запрещает обход {start} для User-Agent '{_options.SiteUserAgent}'.");
        }

        var queue = new Queue<Uri>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start.AbsoluteUri };
        queue.Enqueue(start);
        var position = 0;
        var childrenExpanded = false;

        while (queue.Count > 0 && position < _options.SiteMaxPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = queue.Dequeue();

            string? html;
            try
            {
                html = await FetchHtmlAsync(client, uri, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"GET {uri} не завершился за {_options.SiteRequestTimeout.TotalSeconds:0} с.");
            }
            catch (Exception exception) when (position > 0)
            {
                // Ошибка дочерней страницы не отменяет инжест: остальные страницы индексируются.
                _logger.LogWarning(
                    exception,
                    "Knowledge site crawl skipped {Url}: {Error}",
                    uri,
                    exception.Message);
                continue;
            }

            if (html is null)
            {
                continue;
            }

            var title = HtmlText.ExtractTitle(html, position == 0 ? request.Title : uri.AbsolutePath);
            var content = HtmlText.ToPlainText(html);

            if (content.Length > 0)
            {
                yield return new KnowledgePage(title, content, title, position, uri.AbsoluteUri);
                position++;
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

    private async Task<string?> FetchHtmlAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
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
            return null;
        }

        return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
    }
}
