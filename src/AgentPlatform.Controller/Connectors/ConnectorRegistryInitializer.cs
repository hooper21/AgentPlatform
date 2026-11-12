using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentPlatform.Controller.Connectors;

/// <summary>
/// При старте Controller импортирует реестр коннекторов из Connector Host
/// (несколько попыток: в docker host может подниматься позже). Ошибка импорта
/// не останавливает Controller: каталог можно обновить вручную через API.
/// </summary>
public sealed class ConnectorRegistryInitializer(
    IServiceScopeFactory scopeFactory,
    ILogger<ConnectorRegistryInitializer> logger) : BackgroundService
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory _scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    private readonly ILogger<ConnectorRegistryInitializer> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var sync = scope.ServiceProvider.GetRequiredService<ConnectorRegistrySyncService>();
                var synced = await sync.SyncEchoAsync(stoppingToken).ConfigureAwait(false);

                if (synced is not null)
                {
                    _logger.LogInformation(
                        "Connector registry synced: {Name} {Version}",
                        synced.Name,
                        synced.Version);
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Connector registry initial sync attempt {Attempt} failed", attempt);
            }

            try
            {
                await Task.Delay(RetryDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        _logger.LogWarning(
            "Connector registry was not synced from Connector Host; " +
            "use POST /api/v1/internal/connector-registry/sync to retry");
    }
}
