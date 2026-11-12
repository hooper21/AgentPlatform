namespace AgentPlatform.Runtime;

/// <summary>
/// Настройки Runtime агентов (MafOrchestrationRuntime): кеш конфигурации агента,
/// таймауты гейтов и страховочные лимиты графа.
/// </summary>
public sealed class AgentRuntimeOptions
{
    /// <summary>
    /// TTL кеша конфигурации агента (по требованию платформы — 60 секунд).
    /// Инвалидация при изменении конфигурации выполняется явно
    /// (<see cref="Orchestration.AgentFactory.InvalidateAsync"/>).
    /// </summary>
    public TimeSpan ConfigCacheTtl { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Строка подключения Redis для кеша конфигурации. null — кеш в памяти процесса
    /// (dev/тесты); при недоступном Redis кеш отключается, конфигурация грузится из БД.
    /// </summary>
    public string? RedisConnectionString { get; set; }

    /// <summary>
    /// Срок жизни ожидающего HITL-гейта для тенантов без отдельной настройки.
    /// По умолчанию 24 часа (требование платформы).
    /// </summary>
    public TimeSpan GateExpiry { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Настройка срока жизни гейта для конкретного тенанта. Переопределяет
    /// <see cref="GateExpiry"/>; таймаут фиксируется в заявке approvals при паузе.
    /// </summary>
    public Dictionary<Guid, TimeSpan> GateExpiryOverrides { get; set; } = [];

    /// <summary>Срок жизни гейта тенанта: override тенанта или значение по умолчанию.</summary>
    public TimeSpan GetGateExpiry(Guid tenantId)
        => GateExpiryOverrides.TryGetValue(tenantId, out var timeout) ? timeout : GateExpiry;

    /// <summary>
    /// Страховка от зацикливания графа: максимум suперстепов на один RunAsync/ResumeAsync.
    /// Основные лимиты берутся из версии агента (max_turns / max_tool_calls).
    /// </summary>
    public int MaxSuperstepsPerTurn { get; set; } = 32;

    /// <summary>
    /// Создаёт кеш конфигурации: Redis при заданной строке подключения, иначе in-memory.
    /// </summary>
    public IAgentConfigCache CreateConfigCache()
        => string.IsNullOrWhiteSpace(RedisConnectionString)
            ? new InMemoryAgentConfigCache(ConfigCacheTtl)
            : new RedisAgentConfigCache(RedisConnectionString, ConfigCacheTtl);
}
