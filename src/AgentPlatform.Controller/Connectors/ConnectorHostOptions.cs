namespace AgentPlatform.Controller.Connectors;

/// <summary>
/// Настройки MCP-транспорта к Connector Host. Платформенные коннекторы
/// (EchoTools) хостятся внутри платформы, внешние — по connector_installations.external_url.
/// </summary>
public sealed class ConnectorHostOptions
{
    /// <summary>Базовый URL Connector Host (для внутреннего EchoTools).</summary>
    public string BaseUrl { get; set; } = "http://localhost:8083";

    /// <summary>Путь MCP endpoint-а внутри Connector Host.</summary>
    public string McpPath { get; set; } = "/mcp";

    /// <summary>Таймаут вызова тула (секунды).</summary>
    public int CallTimeoutSeconds { get; set; } = 30;

    /// <summary>Возвращает MCP endpoint внутреннего Connector Host.</summary>
    public Uri ResolveInternalEndpoint()
    {
        var baseUri = new Uri(BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        var path = McpPath.TrimStart('/');

        return new Uri(baseUri, path);
    }
}
