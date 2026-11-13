using System.Text.Json;
using AgentPlatform.Core.Dto;
using AgentPlatform.Core.Json;
using AgentPlatform.Infrastructure.Persistence.Entities;
using AgentPlatform.Knowledge.Contracts;

namespace AgentPlatform.Knowledge.Ingest;

/// <summary>
/// Сериализация конфигурации подключения в knowledge_sources.config (jsonb).
/// Старые строки имеют config = '{}': для них значения собираются из legacy-полей
/// (ref, index_meta.manual_content, credentials_ref), поэтому поведение 2.1
/// не меняется.
/// </summary>
public static class KnowledgeProviderConfigJson
{
    /// <summary>Пишет конфигурацию в jsonb (snake_case, как принято в БД платформы).</summary>
    public static string ToJson(KnowledgeProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return PlatformJson.SerializeDb(config);
    }

    /// <summary>Читает конфигурацию; null — config пуст или повреждён.</summary>
    public static KnowledgeProviderConfig? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var config = PlatformJson.DeserializeDb<KnowledgeProviderConfig>(json);

            // '{}' — конфигурации нет: значения берутся из legacy-полей источника.
            var isEmpty = config is null
                || string.IsNullOrWhiteSpace(config.Title)
                && config.Location is null
                && config.Content is null
                && config.CredentialsRef is null
                && (config.Parameters is null || config.Parameters.Count == 0);

            return isEmpty ? null : config;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Собирает конфигурацию из jsonb config с фолбэком на legacy-поля источника.
    /// </summary>
    public static KnowledgeProviderConfig FromSource(KnowledgeSource source, KnowledgeIndexMeta meta)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(meta);

        var parsed = Parse(source.Config);

        return new KnowledgeProviderConfig(
            Title: FirstNonEmpty(parsed?.Title) ?? source.Title,
            Location: FirstNonEmpty(parsed?.Location) ?? FirstNonEmpty(source.Ref),
            Content: parsed?.Content ?? meta.ManualContent,
            CredentialsRef: FirstNonEmpty(parsed?.CredentialsRef) ?? FirstNonEmpty(source.CredentialsRef),
            Parameters: parsed?.Parameters);
    }

    private static string? FirstNonEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
