using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Dto;
using Microsoft.Extensions.Logging;

namespace AgentPlatform.Knowledge.Ingest.Providers.Confluence;

/// <summary>
/// Провайдер Confluence: облачный Atlassian (OAuth 2.0, REST API v2) и
/// on-premise Confluence Server/Data Center (API-токен/логин, REST API v1).
/// <para>
/// Индексируется только то, к чему есть доступ у подключённого токена:
/// постраничные права Confluence в диалоге НЕ проверяются, поэтому мастер
/// подключения показывает обязательное предупреждение (см. <see cref="Warning"/>).
/// </para>
/// <para>
/// Первый прогон — полная выгрузка выбранных пространств (storage → plain text).
/// Инкремент — по дате изменения (курсор) и по вебхукам
/// page_created/page_updated/page_trashed. Медиа не индексируются (TODO фазы 4).
/// </para>
/// <remarks>
/// Эндпоинты сверены с официальной документацией:
/// Cloud v2 — developer.atlassian.com/cloud/confluence/rest/v2 (pages/spaces/ancestors),
/// Cloud OAuth 2.0 (3LO) — accessible-resources + api.atlassian.com/ex/confluence/{cloudId},
/// Server/DC v1 — GET /rest/api/space, /rest/api/content, /rest/api/content/search (CQL).
/// </remarks>
/// </summary>
public sealed partial class ConfluenceProvider(
    IHttpClientFactory httpClientFactory,
    ConfluenceOptions options,
    IKnowledgeSecretResolver secrets,
    TimeProvider timeProvider,
    ILogger<ConfluenceProvider> logger) : IKnowledgeProvider, IKnowledgeProviderSetup, IKnowledgeWebhookParser
{
    /// <summary>Токен провайдера (knowledge_sources.provider_type / type).</summary>
    public const string ProviderType = "confluence";

    /// <summary>Имя HttpClient в DI.</summary>
    public const string HttpClientName = "knowledge-confluence";

    /// <summary>config: cloud | onpremise.</summary>
    public const string InstanceTypeParameter = "instanceType";

    /// <summary>config: базовый URL сайта (обязателен для on-premise).</summary>
    public const string BaseUrlParameter = "baseUrl";

    /// <summary>config: oauth2 (cloud) | apiToken (on-premise).</summary>
    public const string AuthTypeParameter = "authType";

    /// <summary>config: ключи выбранных пространств через запятую.</summary>
    public const string SpacesParameter = "spaces";

    /// <summary>config: предохранитель числа страниц на пространство.</summary>
    public const string MaxPagesParameter = "maxPages";

    /// <summary>config: перекрытие окна инкремента в минутах.</summary>
    public const string OverlapMinutesParameter = "overlapMinutes";

    /// <summary>
    /// Обязательное предупреждение шага подключения: права доступа не переносятся
    /// в чат, индексируется всё, что доступно токену.
    /// </summary>
    public const string Warning =
        "В чат будут отвечать по всему содержимому выбранных пространств, доступному этому токену. " +
        "Постраничные права доступа Confluence не проверяются в диалоге";

    internal const string CloudInstance = "cloud";
    internal const string OnPremiseInstance = "onpremise";
    internal const string OAuth2Auth = "oauth2";
    internal const string ApiTokenAuth = "apiToken";

    [GeneratedRegex(@"^[A-Za-z0-9~._-]{1,255}$", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceKeyPattern();

    private readonly IHttpClientFactory _httpClientFactory =
        httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));

    private readonly ConfluenceOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    private readonly IKnowledgeSecretResolver _secrets =
        secrets ?? throw new ArgumentNullException(nameof(secrets));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<ConfluenceProvider> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    string IKnowledgeProviderSetup.ProviderType => ProviderType;

    /// <inheritdoc />
    string IKnowledgeProviderSetup.Warning => Warning;

    /// <inheritdoc />
    public KnowledgeProviderInfo GetSourceInfo() => new(
        ProviderType,
        "Confluence",
        ProviderType,
        "Страницы выбранных пространств Confluence: облако (REST API v2, OAuth 2.0) " +
        "или on-premise Server/DC (REST API v1, API-токен/логин).",
        [
            new KnowledgeProviderParameter(
                InstanceTypeParameter,
                "Тип инсталляции",
                Required: true,
                Description: "cloud — облачный Atlassian (REST API v2); onpremise — Server/Data Center (REST API v1).",
                DefaultValue: CloudInstance),
            new KnowledgeProviderParameter(
                BaseUrlParameter,
                "Базовый URL",
                Description: "https://<site>.atlassian.net/wiki для облака (если токену доступно несколько сайтов) " +
                             "или https://confluence.example.local/confluence для on-premise."),
            new KnowledgeProviderParameter(
                AuthTypeParameter,
                "Аутентификация",
                Required: true,
                Description: "oauth2 — OAuth 2.0 (облако, секрет: accessToken/cloudId); " +
                             "apiToken — логин/API-токен или personalAccessToken (on-premise).",
                DefaultValue: OAuth2Auth),
            new KnowledgeProviderParameter(
                SpacesParameter,
                "Пространства",
                Required: true,
                Description: "Ключи пространств; выбираются в мастере после TestConnection."),
            new KnowledgeProviderParameter(
                MaxPagesParameter,
                "Максимум страниц на пространство",
                Description: "Предохранитель полной выгрузки; по умолчанию — Knowledge:Confluence:MaxPagesPerSpace.",
                DefaultValue: _options.MaxPagesPerSpace.ToString(CultureInfo.InvariantCulture)),
            new KnowledgeProviderParameter(
                OverlapMinutesParameter,
                "Перекрытие инкремента, мин",
                Description: "Страховочное окно инкрементальной синхронизации (0..1440).",
                DefaultValue: ((int)_options.ModifiedOverlap.TotalMinutes).ToString(CultureInfo.InvariantCulture))
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

        try
        {
            using var session = await OpenSessionAsync(config, requireSpaces: false, cancellationToken)
                .ConfigureAwait(false);

            using var document = session.Instance == CloudInstance
                ? await session.Client
                    .GetJsonAsync(new Uri(session.ApiBase, "spaces?limit=1"), cancellationToken)
                    .ConfigureAwait(false)
                : await session.Client
                    .GetJsonAsync(new Uri(session.ApiBase, "space?limit=1"), cancellationToken)
                    .ConfigureAwait(false);

            return KnowledgeConnectionStatus.Healthy(
                $"Подключение к Confluence установлено ({DescribeInstance(session.Instance)}).",
                now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Confluence TestConnection failed for '{Title}'",
                config.Title);

            return KnowledgeConnectionStatus.Unhealthy(
                "Не удалось подключиться к Confluence: проверьте URL и учётные данные.",
                now,
                exception.Message);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<KnowledgeSourceSpace>> ListSpacesAsync(
        KnowledgeProviderConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        using var session = await OpenSessionAsync(config, requireSpaces: false, cancellationToken)
            .ConfigureAwait(false);

        return session.Instance == CloudInstance
            ? await ListCloudSpacesAsync(session, cancellationToken).ConfigureAwait(false)
            : await ListOnPremiseSpacesAsync(session, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<KnowledgePage> FetchAll(
        KnowledgeProviderConfig config,
        DateTimeOffset? since = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var startedAt = _timeProvider.GetUtcNow();
        using var session = await OpenSessionAsync(config, requireSpaces: true, cancellationToken)
            .ConfigureAwait(false);

        var cursor = FormatCursor(startedAt - ResolveOverlap(session.Settings));

        foreach (var spaceKey in session.Settings.Spaces)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pages = await LoadFullPagesAsync(session, spaceKey, cancellationToken).ConfigureAwait(false);

            foreach (var page in pages)
            {
                if (since is { } threshold && page.ModifiedAt is { } modified && modified < threshold)
                {
                    continue;
                }

                yield return MapPage(session, page, cursor);
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<KnowledgeChange> FetchIncremental(
        KnowledgeProviderConfig config,
        string? cursor = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var startedAt = _timeProvider.GetUtcNow();
        using var session = await OpenSessionAsync(config, requireSpaces: true, cancellationToken)
            .ConfigureAwait(false);

        var parsedCursor = ParseCursor(cursor);
        var overlap = ResolveOverlap(session.Settings);

        if (parsedCursor is null)
        {
            // Курсора нет: полная выгрузка, отданная как изменения.
            var fullCursor = FormatCursor(startedAt - overlap);

            foreach (var spaceKey in session.Settings.Spaces)
            {
                var pages = await LoadFullPagesAsync(session, spaceKey, cancellationToken).ConfigureAwait(false);

                foreach (var page in pages)
                {
                    yield return new KnowledgeChange(MapPage(session, page, fullCursor), fullCursor);
                }
            }

            yield break;
        }

        var changed = new List<ConfluencePageData>();
        var since = parsedCursor.Value - overlap;

        foreach (var spaceKey in session.Settings.Spaces)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pages = session.Instance == CloudInstance
                ? await LoadCloudChangesAsync(session, spaceKey, parsedCursor.Value, since, cancellationToken)
                    .ConfigureAwait(false)
                : await LoadOnPremiseChangesAsync(session, spaceKey, parsedCursor.Value, since, cancellationToken)
                    .ConfigureAwait(false);

            changed.AddRange(pages);
        }

        var next = parsedCursor.Value;
        var relevant = new List<ConfluencePageData>(changed.Count);

        foreach (var page in changed)
        {
            if (page.ModifiedAt is { } modified)
            {
                // Уже применённые страницы не индексируются повторно.
                if (modified <= parsedCursor.Value)
                {
                    continue;
                }

                if (modified > next)
                {
                    next = modified;
                }
            }

            relevant.Add(page);
        }

        if (relevant.Count == 0)
        {
            // Пустой поток: курсор остаётся прежним (изменений нет).
            yield break;
        }

        var nextCursor = FormatCursor(next);

        foreach (var page in relevant)
        {
            yield return new KnowledgeChange(MapPage(session, page, nextCursor), nextCursor);
        }
    }

    /// <inheritdoc />
    public bool CanParse(string? providerType)
        => string.Equals(providerType, ProviderType, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public bool TryParse(JsonElement payload, out KnowledgeWebhookEvent @event, string? eventHint = null)
    {
        @event = null!;

        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var eventName = ReadString(payload, "event")
            ?? ReadString(payload, "eventType")
            ?? ReadString(payload, "event_type")
            ?? FirstNonEmpty(eventHint);

        var page = TryGetProperty(payload, "page", out var pageElement) && pageElement.ValueKind == JsonValueKind.Object
            ? pageElement
            : payload;

        var externalId = ReadId(page, "id")
            ?? ReadString(payload, "pageId")
            ?? ReadString(payload, "page_id")
            ?? ReadString(payload, "contentId");

        var eventType = ClassifyEvent(eventName);

        if (eventType is null
            && !string.IsNullOrWhiteSpace(externalId)
            && TryGetProperty(payload, "page", out _))
        {
            // Connect не кладёт имя события в тело: тип приходит из регистрации
            // вебхука (eventHint). Для trash статус может быть в самом page.
            var status = ReadString(page, "status")
                ?? ReadString(page, "contentStatus")
                ?? ReadString(page, "content_status");

            eventType = status?.Contains("trash", StringComparison.OrdinalIgnoreCase) == true
                ? KnowledgeWebhookEventTypes.Trashed
                : KnowledgeWebhookEventTypes.Updated;
        }

        if (eventType is null || string.IsNullOrWhiteSpace(externalId))
        {
            return false;
        }

        var spaceKey = ReadString(page, "spaceKey")
            ?? ReadString(page, "space_key")
            ?? ReadString(payload, "spaceKey");

        @event = new KnowledgeWebhookEvent(
            eventType,
            externalId,
            spaceKey,
            ReadTimestampMillis(payload, "timestamp"),
            eventType == KnowledgeWebhookEventTypes.Trashed);

        return true;
    }

    private static string? ClassifyEvent(string? eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            return null;
        }

        // Connect: page_created / page_updated / page_trashed.
        // Forge: avi:confluence:page:created / ...:updated / ...:trashed.
        var normalized = eventName
            .Replace(':', '_')
            .Replace('.', '_')
            .ToLowerInvariant();

        if (!normalized.Contains("page", StringComparison.Ordinal))
        {
            return null;
        }

        if (normalized.Contains("trash", StringComparison.Ordinal)
            || normalized.Contains("delete", StringComparison.Ordinal)
            || normalized.Contains("remove", StringComparison.Ordinal))
        {
            return KnowledgeWebhookEventTypes.Trashed;
        }

        if (normalized.Contains("created", StringComparison.Ordinal)
            || normalized.Contains("create", StringComparison.Ordinal))
        {
            return KnowledgeWebhookEventTypes.Created;
        }

        if (normalized.Contains("updated", StringComparison.Ordinal)
            || normalized.Contains("update", StringComparison.Ordinal))
        {
            return KnowledgeWebhookEventTypes.Updated;
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadId(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            // Connect payload: page.id — число; Forge/новые payload — строка.
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var number) =>
                number.ToString(CultureInfo.InvariantCulture),
            _ => null
        };
    }

    private static DateTimeOffset? ReadTimestampMillis(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var millis) && millis > 0)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(millis);
        }

        return value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
                out var parsed)
                ? parsed
                : null;
    }

    private async Task<ConfluenceSession> OpenSessionAsync(
        KnowledgeProviderConfig config,
        bool requireSpaces,
        CancellationToken cancellationToken)
    {
        var settings = ParseSettings(config, requireSpaces);

        var credentials = await _secrets
            .ResolveAsync(config.CredentialsRef, cancellationToken)
            .ConfigureAwait(false);

        if (credentials is null || credentials.Count == 0)
        {
            throw new KnowledgeProviderConfigurationException(
                "Не заданы учётные данные Confluence: сохраните токен (credentialsRef) " +
                "или передайте Credentials в запросе мастера.");
        }

        var auth = BuildAuth(settings, credentials);
        var client = new ConfluenceRestClient(
            _httpClientFactory.CreateClient(HttpClientName),
            _options,
            _timeProvider,
            _logger,
            auth);

        try
        {
            if (string.Equals(settings.Instance, CloudInstance, StringComparison.Ordinal))
            {
                var identity = await ResolveCloudIdentityAsync(client, settings, credentials, cancellationToken)
                    .ConfigureAwait(false);

                var apiBase = new Uri(
                    $"{_options.CloudApiRoot.TrimEnd('/')}/ex/confluence/" +
                    $"{Uri.EscapeDataString(identity.CloudId)}/wiki/api/v2/");

                return new ConfluenceSession
                {
                    Instance = settings.Instance,
                    Settings = settings,
                    Client = client,
                    ApiBase = apiBase,
                    WebBase = identity.SiteUrl is null ? null : identity.SiteUrl.TrimEnd('/') + "/wiki"
                };
            }

            var siteRoot = settings.BaseUrl!.AbsoluteUri.TrimEnd('/');

            return new ConfluenceSession
            {
                Instance = settings.Instance,
                Settings = settings,
                Client = client,
                ApiBase = new Uri(siteRoot + "/rest/api/"),
                WebBase = siteRoot
            };
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task<CloudIdentity> ResolveCloudIdentityAsync(
        ConfluenceRestClient client,
        ConfluenceSettings settings,
        IReadOnlyDictionary<string, string> credentials,
        CancellationToken cancellationToken)
    {
        var explicitCloudId = GetCredential(credentials, "cloudId");
        List<CloudResource>? resources = null;

        try
        {
            var uri = new Uri($"{_options.CloudApiRoot.TrimEnd('/')}/oauth/token/accessible-resources");
            using var document = await client.GetJsonAsync(uri, cancellationToken).ConfigureAwait(false);
            resources = ConfluenceRestClient.Deserialize<List<CloudResource>>(document) ?? [];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (string.IsNullOrWhiteSpace(explicitCloudId))
            {
                throw;
            }

            _logger.LogWarning(
                exception,
                "Confluence accessible-resources недоступен: используется cloudId из секрета");
        }

        if (resources is { Count: > 0 })
        {
            if (!string.IsNullOrWhiteSpace(explicitCloudId))
            {
                var byId = resources.FirstOrDefault(resource =>
                    string.Equals(resource.Id, explicitCloudId, StringComparison.Ordinal));

                if (byId is not null)
                {
                    return new CloudIdentity(byId.Id!, byId.Url);
                }
            }

            if (settings.BaseUrl is { } baseUrl)
            {
                var byHost = resources.FirstOrDefault(resource =>
                    Uri.TryCreate(resource.Url, UriKind.Absolute, out var resourceUri)
                    && string.Equals(resourceUri.Host, baseUrl.Host, StringComparison.OrdinalIgnoreCase));

                if (byHost is not null)
                {
                    return new CloudIdentity(byHost.Id!, byHost.Url);
                }
            }

            if (resources.Count == 1 && !string.IsNullOrWhiteSpace(resources[0].Id))
            {
                return new CloudIdentity(resources[0].Id!, resources[0].Url);
            }
        }

        if (!string.IsNullOrWhiteSpace(explicitCloudId))
        {
            return new CloudIdentity(explicitCloudId, settings.BaseUrl?.GetLeftPart(UriPartial.Authority));
        }

        throw new KnowledgeProviderConfigurationException(
            "Токен не даёт доступа к сайту Atlassian: проверьте baseUrl/cloudId и scopes токена.");
    }

    private async Task<IReadOnlyList<KnowledgeSourceSpace>> ListCloudSpacesAsync(
        ConfluenceSession session,
        CancellationToken cancellationToken)
    {
        var spaces = new List<KnowledgeSourceSpace>();
        var limit = Math.Clamp(_options.PageSize, 1, 250);
        var uri = new Uri(session.ApiBase, $"spaces?limit={limit}&status=current");

        for (var guard = 0; uri is not null && guard < 100; guard++)
        {
            using var document = await session.Client.GetJsonAsync(uri, cancellationToken).ConfigureAwait(false);
            var result = ConfluenceRestClient.Deserialize<V2MultiEntity<V2Space>>(document)
                ?? new V2MultiEntity<V2Space>(null, null);

            var webBase = CloudWebBase(result.Links?.Base ?? session.WebBase);

            foreach (var space in result.Results ?? [])
            {
                if (string.IsNullOrWhiteSpace(space.Key))
                {
                    continue;
                }

                spaces.Add(new KnowledgeSourceSpace(
                    space.Key,
                    FirstNonEmpty(space.Name) ?? space.Key,
                    space.Type,
                    BuildWebUrl(webBase, space.Links?.Webui)));

                if (spaces.Count >= _options.MaxSpaces)
                {
                    return spaces;
                }
            }

            uri = ConfluenceRestClient.NextUri(uri, result.Links?.Next);
        }

        return spaces
            .OrderBy(space => space.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(space => space.Key, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<IReadOnlyList<KnowledgeSourceSpace>> ListOnPremiseSpacesAsync(
        ConfluenceSession session,
        CancellationToken cancellationToken)
    {
        var spaces = new List<KnowledgeSourceSpace>();
        var limit = Math.Clamp(_options.PageSize, 1, 100);
        var start = 0;

        for (var guard = 0; guard < 100; guard++)
        {
            var uri = new Uri(
                session.ApiBase,
                $"space?limit={limit}&start={start}&expand=description.plain");

            using var document = await session.Client.GetJsonAsync(uri, cancellationToken).ConfigureAwait(false);
            var result = ConfluenceRestClient.Deserialize<V1Results<V1SpaceResult>>(document)
                ?? new V1Results<V1SpaceResult>(null, null, null, null, null);

            var results = result.Results ?? [];
            var webBase = result.Links?.Base ?? session.WebBase;

            foreach (var space in results)
            {
                if (string.IsNullOrWhiteSpace(space.Key))
                {
                    continue;
                }

                spaces.Add(new KnowledgeSourceSpace(
                    space.Key,
                    FirstNonEmpty(space.Name) ?? space.Key,
                    space.Type,
                    BuildWebUrl(webBase, space.Links?.Webui)));

                if (spaces.Count >= _options.MaxSpaces)
                {
                    return spaces;
                }
            }

            if (results.Count == 0 || results.Count < limit)
            {
                break;
            }

            start = (result.Start ?? start) + results.Count;
        }

        return spaces
            .OrderBy(space => space.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(space => space.Key, StringComparer.Ordinal)
            .ToList();
    }

    private static string? BuildWebUrl(string? baseUrl, string? webui)
    {
        if (string.IsNullOrWhiteSpace(webui))
        {
            return null;
        }

        if (Uri.TryCreate(webui, UriKind.Absolute, out var absolute))
        {
            // Только безопасные схемы: URL попадает в метаданные/цитаты.
            return (string.Equals(absolute.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(absolute.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                ? absolute.ToString()
                : null;
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return webui;
        }

        return baseUrl.TrimEnd('/') + "/" + webui.TrimStart('/');
    }

    private static string? CloudWebBase(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return baseUrl;
        }

        var root = baseUrl.TrimEnd('/');
        return root.EndsWith("/wiki", StringComparison.OrdinalIgnoreCase) ? root : root + "/wiki";
    }

    private async Task<List<ConfluencePageData>> LoadFullPagesAsync(
        ConfluenceSession session,
        string spaceKey,
        CancellationToken cancellationToken)
        => string.Equals(session.Instance, CloudInstance, StringComparison.Ordinal)
            ? await LoadCloudFullPagesAsync(session, spaceKey, cancellationToken).ConfigureAwait(false)
            : await LoadOnPremiseFullPagesAsync(session, spaceKey, cancellationToken).ConfigureAwait(false);

    private async Task<List<ConfluencePageData>> LoadCloudFullPagesAsync(
        ConfluenceSession session,
        string spaceKey,
        CancellationToken cancellationToken)
    {
        var space = await ResolveCloudSpaceAsync(session, spaceKey, cancellationToken).ConfigureAwait(false);
        var limit = Math.Clamp(_options.PageSize, 1, 250);
        var maxPages = ResolveMaxPages(session.Settings);
        var uri = new Uri(
            session.ApiBase,
            $"spaces/{Uri.EscapeDataString(space.Id!)}/pages" +
            $"?body-format=storage&status=current&limit={limit}");

        var pages = new List<V2Page>();
        string? webBase = session.WebBase;

        while (uri is not null)
        {
            using var document = await session.Client.GetJsonAsync(uri, cancellationToken).ConfigureAwait(false);
            var result = ConfluenceRestClient.Deserialize<V2MultiEntity<V2Page>>(document)
                ?? new V2MultiEntity<V2Page>(null, null);

            webBase = result.Links?.Base ?? webBase;
            pages.AddRange(result.Results ?? []);

            if (pages.Count > maxPages)
            {
                throw new InvalidOperationException(
                    $"Пространство {spaceKey} содержит больше {maxPages} страниц: " +
                    $"увеличьте {MaxPagesParameter} или выберите другое пространство.");
            }

            uri = ConfluenceRestClient.NextUri(uri, result.Links?.Next);
        }

        var nodes = BuildCloudNodeMap(pages);

        return pages
            .Where(page => !string.IsNullOrWhiteSpace(page.Id))
            .Select(page => CreateCloudPageData(
                page,
                space.Key ?? spaceKey,
                space.Name,
                ResolveCloudAncestorTitles(page, nodes),
                webBase))
            .ToList();
    }

    private async Task<V2Space> ResolveCloudSpaceAsync(
        ConfluenceSession session,
        string spaceKey,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(
            session.ApiBase,
            $"spaces?keys={Uri.EscapeDataString(spaceKey)}&limit=1");

        using var document = await session.Client.GetJsonAsync(uri, cancellationToken).ConfigureAwait(false);
        var result = ConfluenceRestClient.Deserialize<V2MultiEntity<V2Space>>(document);

        var space = result?.Results?.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, spaceKey, StringComparison.OrdinalIgnoreCase));

        return space ?? throw new ConfluenceApiException(
            404,
            $"Пространство Confluence '{spaceKey}' не найдено или недоступно токену.");
    }

    private async Task<V1SpaceResult> ResolveOnPremiseSpaceAsync(
        ConfluenceSession session,
        string spaceKey,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(
            session.ApiBase,
            $"space/{Uri.EscapeDataString(spaceKey)}?expand=description.plain");

        using var document = await session.Client.GetJsonAsync(uri, cancellationToken).ConfigureAwait(false);
        var space = ConfluenceRestClient.Deserialize<V1SpaceResult>(document);

        return space ?? throw new ConfluenceApiException(
            404,
            $"Пространство Confluence '{spaceKey}' не найдено или недоступно токену.");
    }

    private async Task<List<ConfluencePageData>> LoadOnPremiseFullPagesAsync(
        ConfluenceSession session,
        string spaceKey,
        CancellationToken cancellationToken)
    {
        var space = await ResolveOnPremiseSpaceAsync(session, spaceKey, cancellationToken).ConfigureAwait(false);
        var limit = Math.Clamp(_options.PageSize, 1, 100);
        var maxPages = ResolveMaxPages(session.Settings);
        var pages = new List<V1Content>();
        var start = 0;
        string? webBase = session.WebBase;

        while (true)
        {
            var uri = BuildOnPremiseContentUri(session, spaceKey, start, limit, status: "current");

            using var document = await session.Client.GetJsonAsync(uri, cancellationToken).ConfigureAwait(false);
            var result = ConfluenceRestClient.Deserialize<V1Results<V1Content>>(document)
                ?? new V1Results<V1Content>(null, null, null, null, null);

            webBase = result.Links?.Base ?? webBase;
            var results = result.Results ?? [];
            pages.AddRange(results);

            if (pages.Count > maxPages)
            {
                throw new InvalidOperationException(
                    $"Пространство {spaceKey} содержит больше {maxPages} страниц: " +
                    $"увеличьте {MaxPagesParameter} или выберите другое пространство.");
            }

            if (results.Count == 0 || results.Count < limit)
            {
                break;
            }

            start = (result.Start ?? start) + results.Count;
        }

        return pages.Select(content => CreateOnPremisePageData(content, space, spaceKey, webBase)).ToList();
    }

    private static Dictionary<string, CloudNode> BuildCloudNodeMap(IEnumerable<V2Page> pages)
    {
        var nodes = new Dictionary<string, CloudNode>(StringComparer.Ordinal);

        foreach (var page in pages)
        {
            if (!string.IsNullOrWhiteSpace(page.Id))
            {
                nodes[page.Id] = new CloudNode(page.Title ?? string.Empty, page.ParentId);
            }
        }

        return nodes;
    }

    private static List<string> ResolveCloudAncestorTitles(
        V2Page page,
        IReadOnlyDictionary<string, CloudNode> nodes)
    {
        var titles = new List<string>();
        var parentId = page.ParentId;

        for (var depth = 0; !string.IsNullOrWhiteSpace(parentId) && depth < 64; depth++)
        {
            if (!nodes.TryGetValue(parentId, out var node))
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(node.Title))
            {
                titles.Add(node.Title);
            }

            parentId = node.ParentId;
        }

        titles.Reverse();
        return titles;
    }

    private int ResolveMaxPages(ConfluenceSettings settings)
        => settings.MaxPages ?? _options.MaxPagesPerSpace;

    private static ConfluencePageData CreateCloudPageData(
        V2Page page,
        string spaceKey,
        string? spaceName,
        IReadOnlyList<string> ancestors,
        string? webBase,
        bool isDeleted = false)
    {
        var title = FirstNonEmpty(page.Title) ?? $"Page {page.Id}";

        return new ConfluencePageData(
            page.Id!,
            title,
            isDeleted ? string.Empty : ConfluenceStorageText.ToPlainText(page.Body?.Storage?.Value),
            page.Version?.CreatedAt,
            page.Version?.AuthorId,
            BuildWebUrl(CloudWebBase(webBase), page.Links?.Webui),
            spaceKey,
            spaceName,
            page.ParentId,
            ancestors,
            isDeleted);
    }

    private static ConfluencePageData CreateOnPremisePageData(
        V1Content content,
        V1SpaceResult? space,
        string fallbackSpaceKey,
        string? webBase,
        bool isDeleted = false)
    {
        var spaceKey = FirstNonEmpty(content.Space?.Key) ?? fallbackSpaceKey;
        var ancestors = content.Ancestors?
            .Select(ancestor => FirstNonEmpty(ancestor.Title))
            .Where(title => title is not null)
            .Select(title => title!)
            .ToList() ?? [];

        var title = FirstNonEmpty(content.Title) ?? $"Page {content.Id}";

        return new ConfluencePageData(
            content.Id!,
            title,
            isDeleted ? string.Empty : ConfluenceStorageText.ToPlainText(content.Body?.Storage?.Value),
            content.Version?.When,
            FirstNonEmpty(content.Version?.By?.DisplayName)
                ?? FirstNonEmpty(content.Version?.By?.Username)
                ?? FirstNonEmpty(content.Version?.By?.AccountId),
            BuildWebUrl(content.Links?.Base ?? webBase, content.Links?.Webui),
            spaceKey,
            FirstNonEmpty(content.Space?.Name) ?? space?.Name,
            content.Ancestors?.LastOrDefault()?.Id,
            ancestors,
            isDeleted);
    }

    private static Uri BuildOnPremiseContentUri(
        ConfluenceSession session,
        string spaceKey,
        int start,
        int limit,
        string? status)
    {
        var query =
            $"content?spaceKey={Uri.EscapeDataString(spaceKey)}&type=page" +
            "&expand=body.storage%2Cversion%2Cancestors%2Cspace" +
            $"&limit={limit}&start={start}";

        if (!string.IsNullOrWhiteSpace(status))
        {
            query += $"&status={Uri.EscapeDataString(status)}";
        }

        return new Uri(session.ApiBase, query);
    }

    private async Task<List<ConfluencePageData>> LoadCloudChangesAsync(
        ConfluenceSession session,
        string spaceKey,
        DateTimeOffset cursor,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        var space = await ResolveCloudSpaceAsync(session, spaceKey, cancellationToken).ConfigureAwait(false);
        var limit = Math.Clamp(_options.PageSize, 1, 250);
        var maxPages = ResolveMaxPages(session.Settings);
        var nodes = new Dictionary<string, CloudNode>(StringComparer.Ordinal);
        var changed = new List<V2Page>();
        string? webBase = session.WebBase;

        var uri = new Uri(
            session.ApiBase,
            $"spaces/{Uri.EscapeDataString(space.Id!)}/pages" +
            $"?body-format=storage&status=current&sort=-modified-date&limit={limit}");

        while (uri is not null)
        {
            using var document = await session.Client.GetJsonAsync(uri, cancellationToken).ConfigureAwait(false);
            var result = ConfluenceRestClient.Deserialize<V2MultiEntity<V2Page>>(document)
                ?? new V2MultiEntity<V2Page>(null, null);

            webBase = result.Links?.Base ?? webBase;
            var stop = false;

            foreach (var page in result.Results ?? [])
            {
                if (string.IsNullOrWhiteSpace(page.Id))
                {
                    continue;
                }

                // Список отсортирован по -modified-date: дальше только старее.
                if (page.Version?.CreatedAt is { } modified && modified < since)
                {
                    stop = true;
                    break;
                }

                changed.Add(page);
                nodes[page.Id] = new CloudNode(page.Title ?? string.Empty, page.ParentId);

                if (changed.Count > maxPages)
                {
                    throw new InvalidOperationException(
                        $"Пространство {spaceKey} изменилось более чем в {maxPages} страницах: " +
                        "выполните полную переиндексацию.");
                }
            }

            if (stop)
            {
                break;
            }

            uri = ConfluenceRestClient.NextUri(uri, result.Links?.Next);
        }

        var output = new List<ConfluencePageData>(changed.Count);

        foreach (var page in changed)
        {
            var ancestors = await ResolveCloudAncestorTitlesAsync(session, page, nodes, cancellationToken)
                .ConfigureAwait(false);

            output.Add(CreateCloudPageData(
                page,
                space.Key ?? spaceKey,
                space.Name,
                ancestors,
                webBase));
        }

        // Удалённые (trash) страницы — tombstone: чанки удаляются по external_id.
        var trashedUri = new Uri(
            session.ApiBase,
            $"spaces/{Uri.EscapeDataString(space.Id!)}/pages" +
            $"?status=trashed&sort=-modified-date&limit={limit}");

        while (trashedUri is not null)
        {
            using var document = await session.Client.GetJsonAsync(trashedUri, cancellationToken).ConfigureAwait(false);
            var result = ConfluenceRestClient.Deserialize<V2MultiEntity<V2Page>>(document)
                ?? new V2MultiEntity<V2Page>(null, null);

            webBase = result.Links?.Base ?? webBase;
            var stop = false;

            foreach (var page in result.Results ?? [])
            {
                if (string.IsNullOrWhiteSpace(page.Id))
                {
                    continue;
                }

                var modified = page.Version?.CreatedAt;

                if (modified is { } value && value < since)
                {
                    stop = true;
                    break;
                }

                if (modified is null || modified > cursor)
                {
                    output.Add(CreateCloudPageData(
                        page,
                        space.Key ?? spaceKey,
                        space.Name,
                        [],
                        webBase,
                        isDeleted: true));
                }
            }

            if (stop)
            {
                break;
            }

            trashedUri = ConfluenceRestClient.NextUri(trashedUri, result.Links?.Next);
        }

        return output;
    }

    private async Task<List<string>> ResolveCloudAncestorTitlesAsync(
        ConfluenceSession session,
        V2Page page,
        Dictionary<string, CloudNode> nodes,
        CancellationToken cancellationToken)
    {
        var titles = new List<string>();
        var parentId = page.ParentId;

        for (var depth = 0; !string.IsNullOrWhiteSpace(parentId) && depth < 64; depth++)
        {
            CloudNode? node;

            try
            {
                node = await GetCloudNodeAsync(session, parentId, nodes, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ConfluenceApiException exception) when (exception.StatusCode == 404)
            {
                // Родитель недоступен токену/удалён: путь обрывается, страница индексируется.
                break;
            }

            if (node is null)
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(node.Title))
            {
                titles.Add(node.Title);
            }

            parentId = node.ParentId;
        }

        titles.Reverse();
        return titles;
    }

    private static async Task<CloudNode?> GetCloudNodeAsync(
        ConfluenceSession session,
        string pageId,
        Dictionary<string, CloudNode> nodes,
        CancellationToken cancellationToken)
    {
        if (nodes.TryGetValue(pageId, out var cached))
        {
            return cached;
        }

        using var document = await session.Client
            .GetJsonAsync(new Uri(session.ApiBase, $"pages/{Uri.EscapeDataString(pageId)}"), cancellationToken)
            .ConfigureAwait(false);

        var page = ConfluenceRestClient.Deserialize<V2Page>(document);

        if (string.IsNullOrWhiteSpace(page?.Id))
        {
            return null;
        }

        var node = new CloudNode(page.Title ?? string.Empty, page.ParentId);
        nodes[pageId] = node;
        return node;
    }

    private async Task<List<ConfluencePageData>> LoadOnPremiseChangesAsync(
        ConfluenceSession session,
        string spaceKey,
        DateTimeOffset cursor,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        // TODO: на Server/DC время lastmodified может трактоваться в таймзоне
        // инсталляции; контракт часов проверяется при приёмке инсталляции.
        // Удаления на v1 не видны в CQL: tombstone приходит вебхуком page_trashed
        // (TODO: contract — регистрация вебхуков и проверка подписи).
        var space = await ResolveOnPremiseSpaceAsync(session, spaceKey, cancellationToken).ConfigureAwait(false);
        var limit = Math.Clamp(_options.PageSize, 1, 100);
        var maxPages = ResolveMaxPages(session.Settings);
        var cql = BuildOnPremiseCql(spaceKey, since);
        var pages = new List<V1Content>();
        var start = 0;
        string? webBase = session.WebBase;

        while (true)
        {
            var uri = new Uri(
                session.ApiBase,
                $"content/search?cql={Uri.EscapeDataString(cql)}" +
                "&expand=body.storage%2Cversion%2Cancestors%2Cspace" +
                $"&limit={limit}&start={start}");

            using var document = await session.Client.GetJsonAsync(uri, cancellationToken).ConfigureAwait(false);
            var result = ConfluenceRestClient.Deserialize<V1Results<V1Content>>(document)
                ?? new V1Results<V1Content>(null, null, null, null, null);

            webBase = result.Links?.Base ?? webBase;
            var results = result.Results ?? [];
            pages.AddRange(results);

            if (pages.Count > maxPages)
            {
                throw new InvalidOperationException(
                    $"Пространство {spaceKey} изменилось более чем в {maxPages} страницах: " +
                    "выполните полную переиндексацию.");
            }

            if (results.Count == 0 || results.Count < limit)
            {
                break;
            }

            start = (result.Start ?? start) + results.Count;
        }

        return pages
            .Where(content => !string.IsNullOrWhiteSpace(content.Id))
            .Where(content => content.Version?.When is not { } modified || modified > cursor)
            .Select(content => CreateOnPremisePageData(content, space, spaceKey, webBase))
            .ToList();
    }

    private static string BuildOnPremiseCql(string spaceKey, DateTimeOffset since)
    {
        // Ключ пространства провалидирован SpaceKeyPattern: кавычки/CQL невозможны.
        var date = since.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        return $"type=page AND space in (\"{spaceKey}\") " +
               $"AND lastmodified >= \"{date}\" ORDER BY lastmodified ASC";
    }

    private static KnowledgePage MapPage(
        ConfluenceSession session,
        ConfluencePageData page,
        string cursor)
    {
        var folder = string.Join(
            " → ",
            page.AncestorTitles.Where(title => !string.IsNullOrWhiteSpace(title)));

        var hierarchy = new List<string>();

        if (!string.IsNullOrWhiteSpace(page.SpaceName))
        {
            hierarchy.Add(page.SpaceName);
        }

        hierarchy.AddRange(page.AncestorTitles);
        hierarchy.Add(page.Title);

        var extra = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["page_id"] = page.ExternalId,
            ["parent_id"] = page.ParentId,
            ["space_key"] = page.SpaceKey,
            ["space_name"] = page.SpaceName,
            ["hierarchy"] = string.Join(" → ", hierarchy),
            ["instance"] = session.Instance
        };

        return new KnowledgePage(
            ExternalId: page.ExternalId,
            Title: page.Title,
            Content: page.Content,
            Metadata: new KnowledgePageMetadata(
                Author: page.Author,
                ModifiedAt: page.ModifiedAt,
                Url: page.Url,
                Space: page.SpaceKey,
                Folder: string.IsNullOrWhiteSpace(folder) ? null : folder,
                Extra: extra),
            IsDeleted: page.IsDeleted,
            Heading: page.Title,
            Position: 0,
            Cursor: cursor);
    }

    private static ConfluenceSettings ParseSettings(KnowledgeProviderConfig config, bool requireSpaces)
    {
        var instance = NormalizeInstance(config.GetParameter(InstanceTypeParameter))
            ?? throw new KnowledgeProviderConfigurationException(
                $"{InstanceTypeParameter} должен быть 'cloud' или 'onpremise'.");

        var authType = NormalizeAuth(config.GetParameter(AuthTypeParameter))
            ?? (string.Equals(instance, CloudInstance, StringComparison.Ordinal) ? OAuth2Auth : ApiTokenAuth);

        var rawBaseUrl = FirstNonEmpty(config.GetParameter(BaseUrlParameter), config.Location);
        Uri? baseUrl = null;

        if (rawBaseUrl is not null)
        {
            if (!Uri.TryCreate(rawBaseUrl, UriKind.Absolute, out var parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                throw new KnowledgeProviderConfigurationException(
                    $"{BaseUrlParameter} должен быть абсолютным http(s)-URL.");
            }

            baseUrl = parsed;
        }

        if (string.Equals(instance, OnPremiseInstance, StringComparison.Ordinal) && baseUrl is null)
        {
            throw new KnowledgeProviderConfigurationException(
                "Для on-premise Confluence обязателен baseUrl " +
                "(например, https://confluence.example.local/confluence).");
        }

        var spaces = ParseSpaceKeys(config.GetParameter(SpacesParameter));

        if (requireSpaces && spaces.Count == 0)
        {
            throw new KnowledgeProviderConfigurationException(
                "Выберите хотя бы одно пространство Confluence (параметр spaces).");
        }

        var maxPages = ParseInt(config.GetParameter(MaxPagesParameter));
        var overlapMinutes = ParseInt(config.GetParameter(OverlapMinutesParameter));

        return new ConfluenceSettings(
            instance,
            authType,
            baseUrl,
            spaces,
            maxPages is > 0 ? maxPages : null,
            overlapMinutes is null
                ? null
                : TimeSpan.FromMinutes(Math.Clamp(overlapMinutes.Value, 0, 24 * 60)));
    }

    private static ConfluenceAuth BuildAuth(
        ConfluenceSettings settings,
        IReadOnlyDictionary<string, string> credentials)
    {
        if (string.Equals(settings.AuthType, OAuth2Auth, StringComparison.Ordinal))
        {
            // TODO: contract — refresh token flow (client_id/client_secret платформы,
            // https://auth.atlassian.com/oauth/token). Сейчас используется access token
            // из Secret Service; истёкший токен даёт 401 и ошибку в index_meta.
            var accessToken = GetCredential(credentials, "accessToken")
                ?? GetCredential(credentials, "token");

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new KnowledgeProviderConfigurationException(
                    "Для OAuth 2.0 нужен accessToken в секрете источника.");
            }

            return ConfluenceAuth.Bearer(accessToken);
        }

        var personalAccessToken = GetCredential(credentials, "personalAccessToken");
        if (!string.IsNullOrWhiteSpace(personalAccessToken))
        {
            return ConfluenceAuth.Bearer(personalAccessToken);
        }

        var username = GetCredential(credentials, "username")
            ?? GetCredential(credentials, "login")
            ?? GetCredential(credentials, "email");

        var apiToken = GetCredential(credentials, "apiToken")
            ?? GetCredential(credentials, "password")
            ?? GetCredential(credentials, "token");

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(apiToken))
        {
            throw new KnowledgeProviderConfigurationException(
                "Для API-токена нужны username и apiToken (или personalAccessToken) в секрете источника.");
        }

        return ConfluenceAuth.Basic(username, apiToken);
    }

    private static string? GetCredential(IReadOnlyDictionary<string, string> credentials, string name)
    {
        if (credentials.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        // Имена полей секрета вводятся в UI и могут отличаться регистром.
        foreach (var pair in credentials)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(pair.Value))
            {
                return pair.Value.Trim();
            }
        }

        return null;
    }

    private static string? NormalizeInstance(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return CloudInstance;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "cloud" or "atlassian" or "atlassian-cloud" => CloudInstance,
            "onpremise" or "on-premise" or "on_premise" or "server" or "datacenter" or "data-center" or "dc"
                => OnPremiseInstance,
            _ => null
        };
    }

    private static string? NormalizeAuth(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "oauth2" or "oauth" or "oauth2.0" or "oauth-2" => OAuth2Auth,
            "apitoken" or "api-token" or "api_token" or "token" or "basic" or "personalaccesstoken"
                => ApiTokenAuth,
            _ => null
        };
    }

    private static IReadOnlyList<string> ParseSpaceKeys(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in raw.Split(
            [',', ';', ' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries))
        {
            var key = candidate.Trim().Trim('"');

            if (!SpaceKeyPattern().IsMatch(key))
            {
                throw new KnowledgeProviderConfigurationException(
                    $"Некорректный ключ пространства Confluence: '{key}'.");
            }

            if (seen.Add(key))
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    private static int? ParseInt(string? raw)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string FormatCursor(DateTimeOffset value)
        => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseCursor(string? cursor)
        => DateTimeOffset.TryParse(
            cursor,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var value)
            ? value
            : null;

    private static string DescribeInstance(string instance)
        => string.Equals(instance, CloudInstance, StringComparison.Ordinal)
            ? "облако, REST API v2"
            : "on-premise, REST API v1";

    private TimeSpan ResolveOverlap(ConfluenceSettings settings)
        => settings.Overlap ?? _options.ModifiedOverlap;

    private sealed record ConfluenceSettings(
        string Instance,
        string AuthType,
        Uri? BaseUrl,
        IReadOnlyList<string> Spaces,
        int? MaxPages,
        TimeSpan? Overlap);

    private sealed record CloudIdentity(string CloudId, string? SiteUrl);

    private sealed record CloudNode(string Title, string? ParentId);

    private sealed class ConfluenceSession : IDisposable
    {
        public required string Instance { get; init; }

        public required ConfluenceSettings Settings { get; init; }

        public required ConfluenceRestClient Client { get; init; }

        public required Uri ApiBase { get; init; }

        /// <summary>Корень веб-интерфейса (cloud: .../wiki, onprem: baseUrl) или null.</summary>
        public string? WebBase { get; init; }

        public void Dispose() => Client.Dispose();
    }

    private sealed record ConfluencePageData(
        string ExternalId,
        string Title,
        string Content,
        DateTimeOffset? ModifiedAt,
        string? Author,
        string? Url,
        string SpaceKey,
        string? SpaceName,
        string? ParentId,
        IReadOnlyList<string> AncestorTitles,
        bool IsDeleted = false);

    // ---- REST-контракты Atlassian (имена полей из официального OpenAPI) ----

    private sealed record CloudResource(string? Id, string? Url, string? Name);

    private sealed record V2MultiEntity<T>(
        List<T>? Results,
        [property: JsonPropertyName("_links")] V2Links? Links);

    private sealed record V2Links(string? Next, string? Base, string? Webui);

    private sealed record V2Space(
        string? Id,
        string? Key,
        string? Name,
        string? Type,
        [property: JsonPropertyName("_links")] V2Links? Links);

    private sealed record V2Page(
        string? Id,
        string? Status,
        string? Title,
        string? SpaceId,
        string? ParentId,
        V2Version? Version,
        V2Body? Body,
        [property: JsonPropertyName("_links")] V2Links? Links);

    private sealed record V2Version(
        DateTimeOffset? CreatedAt,
        int? Number,
        string? AuthorId);

    private sealed record V2Body(V2Storage? Storage);

    private sealed record V2Storage(string? Value);

    private sealed record V1Results<T>(
        List<T>? Results,
        int? Size,
        int? Limit,
        int? Start,
        [property: JsonPropertyName("_links")] V1Links? Links);

    private sealed record V1Content(
        string? Id,
        string? Title,
        string? Type,
        string? Status,
        V1Body? Body,
        V1Version? Version,
        List<V1Ancestor>? Ancestors,
        V1Space? Space,
        [property: JsonPropertyName("_links")] V1Links? Links);

    private sealed record V1Body(V1Storage? Storage);

    private sealed record V1Storage(string? Value);

    private sealed record V1Version(
        DateTimeOffset? When,
        int? Number,
        V1User? By);

    private sealed record V1User(string? DisplayName, string? Username, string? AccountId);

    private sealed record V1Ancestor(string? Id, string? Title);

    private sealed record V1Space(string? Key, string? Name);

    private sealed record V1Links(string? Base, string? Webui, string? Next);

    private sealed record V1SpaceResult(
        string? Id,
        string? Key,
        string? Name,
        string? Type,
        [property: JsonPropertyName("_links")] V1Links? Links);
}
