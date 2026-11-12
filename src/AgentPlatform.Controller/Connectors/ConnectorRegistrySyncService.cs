using System.Security.Cryptography;
using System.Text;
using AgentPlatform.Controller.Contracts;
using AgentPlatform.Core.Enums;
using AgentPlatform.Core.Validation;
using AgentPlatform.Infrastructure.Persistence;
using AgentPlatform.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgentPlatform.Controller.Connectors;

/// <summary>
/// Импорт манифеста внутреннего коннектора EchoTools из Connector Host в
/// connector_registry (глобальная таблица платформы). Так кабинет видит реестр
/// без ручного SQL, а манифест остаётся единственным источником правды в Connector Host.
/// </summary>
public sealed class ConnectorRegistrySyncService(
    AgentPlatformDbContext context,
    IHttpClientFactory httpClientFactory,
    IOptions<ConnectorHostOptions> options,
    ILogger<ConnectorRegistrySyncService> logger)
{
    /// <summary>Имя HTTP-клиента Connector Host.</summary>
    public const string HttpClientName = "connector-host";

    private readonly AgentPlatformDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    private readonly IHttpClientFactory _httpClientFactory =
        httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));

    private readonly ConnectorHostOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));

    private readonly ILogger<ConnectorRegistrySyncService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Читает манифест EchoTools и делает upsert в реестре (null — host недоступен).</summary>
    public async Task<ConnectorCatalogItemResponse?> SyncEchoAsync(CancellationToken cancellationToken = default)
    {
        var baseUri = new Uri(_options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        var manifestUri = new Uri(baseUri, "api/v1/connectors/echo/manifest");

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(manifestUri, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Connector Host manifest is unavailable: {StatusCode} {Url}",
                    (int)response.StatusCode,
                    manifestUri);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var manifest = ConnectorManifestValidator.ParseAndValidate(json);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();

            var entry = await _context.ConnectorRegistry
                .FirstOrDefaultAsync(
                    candidate => candidate.Name == manifest.Name && candidate.Version == manifest.Version,
                    cancellationToken)
                .ConfigureAwait(false);

            if (entry is null)
            {
                entry = new ConnectorRegistryEntry
                {
                    Name = manifest.Name,
                    Version = manifest.Version,
                    Manifest = json,
                    ManifestHash = hash,
                    Publisher = "platform",
                    Status = ConnectorRegistryStatus.Published.ToDbValue()
                };
                _context.ConnectorRegistry.Add(entry);
            }
            else
            {
                entry.Manifest = json;
                entry.ManifestHash = hash;
                entry.Status = ConnectorRegistryStatus.Published.ToDbValue();
            }

            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return new ConnectorCatalogItemResponse(
                entry.Id,
                manifest.Name,
                manifest.Version,
                manifest.Description,
                manifest.Auth.Type.ToDbValue(),
                manifest.Auth.Fields?.ToList() ?? [],
                manifest.Tools.Select(tool => new ConnectorToolCatalogResponse(
                    tool.Name,
                    tool.Description,
                    tool.Policy.ToDbValue(),
                    tool.InputSchema)).ToList());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Connector registry sync from {Url} failed",
                manifestUri);
            return null;
        }
    }
}
