using AgentPlatform.Core.Abstractions;
using AgentPlatform.Core.Dto;
using AgentPlatform.Infrastructure.Persistence.Entities;

namespace AgentPlatform.Knowledge.Ingest;

/// <summary>
/// Реестр провайдеров знаний: единственная точка маршрутизации источника на
/// реализацию <see cref="IKnowledgeProvider"/>. Новый тип источника — новая
/// DI-регистрация провайдера, пайплайн и API не меняются.
/// </summary>
public sealed class KnowledgeProviderRegistry
{
    private readonly Dictionary<string, IKnowledgeProvider> _byType =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlyList<KnowledgeProviderInfo> _infos;

    public KnowledgeProviderRegistry(IEnumerable<IKnowledgeProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var registered = new List<IKnowledgeProvider>();

        foreach (var provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);

            var info = provider.GetSourceInfo();

            // provider_type дублируется в legacy-колонку type (varchar(30)):
            // плагин с более длинным токеном не сможет быть сохранён.
            if (string.IsNullOrWhiteSpace(info.ProviderType) || info.ProviderType.Length > 30)
            {
                throw new InvalidOperationException(
                    "Knowledge provider token must be 1..30 characters (knowledge_sources.type).");
            }

            if (!_byType.TryAdd(info.ProviderType, provider))
            {
                throw new InvalidOperationException(
                    $"Knowledge provider '{info.ProviderType}' is registered more than once.");
            }

            registered.Add(provider);
        }

        _infos = registered.Select(provider => provider.GetSourceInfo()).ToList();
    }

    /// <summary>Описания всех зарегистрированных провайдеров (для UI/API).</summary>
    public IReadOnlyList<KnowledgeProviderInfo> Describe() => _infos;

    /// <summary>Описание провайдера по токену или null.</summary>
    public KnowledgeProviderInfo? Find(string? providerType)
        => !string.IsNullOrWhiteSpace(providerType) && _byType.TryGetValue(providerType, out var provider)
            ? provider.GetSourceInfo()
            : null;

    /// <summary>
    /// Разрешает провайдера источника. provider_type = legacy (так мигрировали
    /// старые строки) означает: тип берётся из legacy-колонки knowledge_sources.type.
    /// </summary>
    public IKnowledgeProvider Resolve(string? providerType, string? legacyType = null)
    {
        var key = string.IsNullOrWhiteSpace(providerType)
            || string.Equals(providerType, KnowledgeProviderTypes.Legacy, StringComparison.OrdinalIgnoreCase)
                ? legacyType
                : providerType;

        if (!string.IsNullOrWhiteSpace(key) && _byType.TryGetValue(key, out var provider))
        {
            return provider;
        }

        throw new ArgumentException(
            $"type must be one of {SupportedTypes}, got '{providerType}'.",
            nameof(providerType));
    }

    /// <summary>Провайдер конкретного источника.</summary>
    public IKnowledgeProvider Resolve(KnowledgeSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Resolve(source.ProviderType, source.Type);
    }

    /// <summary>Список поддерживаемых типов в формате сообщения об ошибке.</summary>
    public string SupportedTypes => string.Join(" | ", _infos.Select(info => info.ProviderType));
}
