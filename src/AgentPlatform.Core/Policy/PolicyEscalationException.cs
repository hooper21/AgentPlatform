using AgentPlatform.Core.Enums;

namespace AgentPlatform.Core.Policy;

/// <summary>
/// Попытка повысить политику тула конфигурацией тенанта (например, read → write).
/// Манифест задаёт потолок политики, tool_settings может только понизить её.
/// Нарушение проверяется и в Controller API (валидация конфига), и в Runtime
/// (повторная проверка перед вызовом тула): уже сохранённый некорректный конфиг
/// не даёт агенту прав.
/// </summary>
public sealed class PolicyEscalationException : Exception
{
    public PolicyEscalationException(string? toolName, ToolPolicy manifestPolicy, ToolPolicy tenantPolicy)
        : base(BuildMessage(toolName, manifestPolicy, tenantPolicy))
    {
        ToolName = toolName;
        ManifestPolicy = manifestPolicy;
        TenantPolicy = tenantPolicy;
    }

    /// <summary>Имя тула из манифеста (если известно).</summary>
    public string? ToolName { get; }

    /// <summary>Политика из манифеста коннектора — потолок.</summary>
    public ToolPolicy ManifestPolicy { get; }

    /// <summary>Политика, запрошенная конфигурацией тенанта.</summary>
    public ToolPolicy TenantPolicy { get; }

    private static string BuildMessage(string? toolName, ToolPolicy manifestPolicy, ToolPolicy tenantPolicy)
    {
        var prefix = string.IsNullOrWhiteSpace(toolName) ? "tool_settings" : $"tool '{toolName}'";

        return $"{prefix}: policy escalation is forbidden " +
               $"({manifestPolicy.ToDbValue()} -> {tenantPolicy.ToDbValue()}); only downgrade is allowed";
    }
}
