using System.Text.Json;
using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Contracts;
using AgentPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgentPlatform.Controller.Connectors;

/// <summary>
/// Вызов тулов коннекторов через MCP (streamable HTTP). Слои платформы не обращаются
/// к внешним API напрямую: Controller идёт в Connector Host, который знает транспорт
/// и секреты установки.
/// <para>
/// TODO(2.2) contract: подключение credentials к MCP-клиенту, лимиты (очередь
/// tenant × connector), ретраи и кеш read-тулов.
/// </para>
/// </summary>
public sealed class McpToolInvoker(
    AgentPlatformDbContext context,
    IOptions<ConnectorHostOptions> options,
    ILoggerFactory loggerFactory) : IToolInvoker
{
    private const string ActiveStatus = "active";
    private const string EchoConnectorName = "echo";

    private readonly AgentPlatformDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    private readonly ConnectorHostOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));

    private readonly ILoggerFactory _loggerFactory =
        loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

    /// <inheritdoc />
    public async Task<ToolInvocationResult> InvokeAsync(
        ToolInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var logger = _loggerFactory.CreateLogger<McpToolInvoker>();

        var installation = await _context.ConnectorInstallations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.Id == request.InstallationId
                    && candidate.TenantId == request.TenantId,
                cancellationToken)
            .ConfigureAwait(false);

        if (installation is null)
        {
            return Failed($"connector installation {request.InstallationId} is not found");
        }

        if (!string.Equals(installation.Status, ActiveStatus, StringComparison.Ordinal))
        {
            return Failed($"connector installation {request.InstallationId} is not active");
        }

        var endpoint = await ResolveEndpointAsync(installation, cancellationToken).ConfigureAwait(false);

        if (endpoint is null)
        {
            return Failed(
                "connector has no external URL; platform connectors must be hosted by Connector Host");
        }

        var arguments = ParseArguments(request.ArgumentsJson);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.CallTimeoutSeconds, 1, 300)));

            var transportOptions = new HttpClientTransportOptions
            {
                Endpoint = endpoint,
                Name = $"agentplatform-{request.InstallationId:N}"
            };

            await using var transport = new HttpClientTransport(transportOptions, _loggerFactory);
            await using var client = await McpClient
                .CreateAsync(transport, loggerFactory: _loggerFactory, cancellationToken: timeout.Token)
                .ConfigureAwait(false);

            var result = await client
                .CallToolAsync(request.ToolName, arguments, cancellationToken: timeout.Token)
                .ConfigureAwait(false);

            var text = string.Join(
                "\n",
                result.Content.OfType<TextContentBlock>().Select(block => block.Text));

            return result.IsError == true
                ? new ToolInvocationResult(false, text, text)
                : new ToolInvocationResult(true, text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failed($"connector call timed out after {_options.CallTimeoutSeconds}s");
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "MCP call {ToolName} to {Endpoint} failed (installation {InstallationId})",
                request.ToolName,
                endpoint,
                request.InstallationId);

            return Failed($"connector host call failed: {exception.Message}");
        }
    }

    private async Task<Uri?> ResolveEndpointAsync(
        Infrastructure.Persistence.Entities.ConnectorInstallation installation,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(installation.ExternalUrl))
        {
            return new Uri(installation.ExternalUrl, UriKind.Absolute);
        }

        var connectorName = await _context.ConnectorRegistry
            .AsNoTracking()
            .Where(registry => registry.Id == installation.RegistryId)
            .Select(registry => registry.Name)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return string.Equals(connectorName, EchoConnectorName, StringComparison.Ordinal)
            ? _options.ResolveInternalEndpoint()
            : null;
    }

    private static IReadOnlyDictionary<string, object?> ParseArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, object?>(StringComparer.Ordinal);
            }

            var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                arguments[property.Name] = property.Value.Clone();
            }

            return arguments;
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }
    }

    private static ToolInvocationResult Failed(string error)
        => new(Succeeded: false, Content: string.Empty, Error: error);
}
