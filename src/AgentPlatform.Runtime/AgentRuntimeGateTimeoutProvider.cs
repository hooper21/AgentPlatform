using AgentPlatform.Core.Abstractions;

namespace AgentPlatform.Runtime;

/// <summary>
/// Таймаут HITL-гейта из настроек Runtime: значение по умолчанию (24 ч) плюс
/// переопределения по тенантам (<see cref="AgentRuntimeOptions.GateExpiryOverrides"/>).
/// Отдельный порт позволяет позже заменить источник на настройки тенанта в БД
/// без изменения GateExecutor.
/// </summary>
public sealed class AgentRuntimeGateTimeoutProvider(AgentRuntimeOptions options) : ITenantGateTimeoutProvider
{
    private readonly AgentRuntimeOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public TimeSpan GetGateTimeout(Guid tenantId)
    {
        var timeout = _options.GetGateExpiry(tenantId);

        if (timeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Gate timeout for tenant {tenantId} must be positive, but is {timeout}.");
        }

        return timeout;
    }
}
