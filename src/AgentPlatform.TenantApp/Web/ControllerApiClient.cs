using System.Net.Http.Json;
using System.Text.Json;

namespace AgentPlatform.TenantApp.Web;

/// <summary>Ошибка Controller API (HTTP-статус + сообщение для UI).</summary>
public sealed class CabinetApiException(int statusCode, string message, string? payload = null) : Exception(message)
{
    /// <summary>HTTP-статус ответа Controller.</summary>
    public int StatusCode { get; } = statusCode;

    /// <summary>Сырое тело ответа (для диагностики).</summary>
    public string? Payload { get; } = payload;
}

/// <summary>
/// Типизированный клиент Controller API. Кабинет не читает БД платформы напрямую:
/// все операции (включая решение по гейту → ResumeAsync) идут через Controller.
/// </summary>
public sealed class ControllerApiClient(HttpClient http)
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>Агенты тенанта.</summary>
    public Task<List<AgentDto>> GetAgentsAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => GetAsync<List<AgentDto>>($"api/v1/internal/tenants/{tenantId}/agents", cancellationToken);

    /// <summary>Создаёт агента (версия 1).</summary>
    public Task<AgentDto> CreateAgentAsync(
        Guid tenantId,
        AgentSaveDto agent,
        CancellationToken cancellationToken = default)
        => SendAsync<AgentDto>(
            HttpMethod.Post,
            $"api/v1/internal/tenants/{tenantId}/agents",
            agent,
            cancellationToken);

    /// <summary>Публикует новую версию агента.</summary>
    public Task<AgentDto> UpdateAgentAsync(
        Guid tenantId,
        Guid agentId,
        AgentSaveDto agent,
        CancellationToken cancellationToken = default)
        => SendAsync<AgentDto>(
            HttpMethod.Put,
            $"api/v1/internal/tenants/{tenantId}/agents/{agentId}",
            agent,
            cancellationToken);

    /// <summary>Удаляет агента (если нет сессий/расписаний).</summary>
    public Task DeleteAgentAsync(Guid tenantId, Guid agentId, CancellationToken cancellationToken = default)
        => SendAsync(
            HttpMethod.Delete,
            $"api/v1/internal/tenants/{tenantId}/agents/{agentId}",
            body: null,
            cancellationToken);

    /// <summary>Импортирует манифест EchoTools из Connector Host в реестр платформы.</summary>
    public Task SyncConnectorRegistryAsync(CancellationToken cancellationToken = default)
        => SendAsync(
            HttpMethod.Post,
            "api/v1/internal/connector-registry/sync",
            body: null,
            cancellationToken);

    /// <summary>Каталог коннекторов платформы.</summary>
    public Task<List<ConnectorCatalogDto>> GetCatalogAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
        => GetAsync<List<ConnectorCatalogDto>>(
            $"api/v1/internal/tenants/{tenantId}/connector-registry",
            cancellationToken);

    /// <summary>Установленные коннекторы тенанта.</summary>
    public Task<List<ConnectorInstallationDto>> GetInstallationsAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
        => GetAsync<List<ConnectorInstallationDto>>(
            $"api/v1/internal/tenants/{tenantId}/connector-installations",
            cancellationToken);

    /// <summary>Устанавливает коннектор (credentials шифруются на Controller).</summary>
    public Task<ConnectorInstallationDto> InstallConnectorAsync(
        Guid tenantId,
        ConnectorInstallDto request,
        CancellationToken cancellationToken = default)
        => SendAsync<ConnectorInstallationDto>(
            HttpMethod.Post,
            $"api/v1/internal/tenants/{tenantId}/connector-installations",
            request,
            cancellationToken);

    /// <summary>Меняет статус/URL/credentials установки.</summary>
    public Task<ConnectorInstallationDto> UpdateInstallationAsync(
        Guid tenantId,
        Guid installationId,
        ConnectorInstallUpdateDto request,
        CancellationToken cancellationToken = default)
        => SendAsync<ConnectorInstallationDto>(
            HttpMethod.Put,
            $"api/v1/internal/tenants/{tenantId}/connector-installations/{installationId}",
            request,
            cancellationToken);

    /// <summary>Удаляет установку коннектора.</summary>
    public Task DeleteInstallationAsync(
        Guid tenantId,
        Guid installationId,
        CancellationToken cancellationToken = default)
        => SendAsync(
            HttpMethod.Delete,
            $"api/v1/internal/tenants/{tenantId}/connector-installations/{installationId}",
            body: null,
            cancellationToken);

    /// <summary>Сессии диалогов тенанта.</summary>
    public Task<List<DialogSessionDto>> GetDialogsAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
        => GetAsync<List<DialogSessionDto>>(
            $"api/v1/internal/tenants/{tenantId}/dialogs",
            cancellationToken);

    /// <summary>Начинает диалог из кабинета.</summary>
    public Task<DialogDto> StartDialogAsync(
        Guid tenantId,
        DialogStartDto request,
        CancellationToken cancellationToken = default)
        => SendAsync<DialogDto>(
            HttpMethod.Post,
            $"api/v1/internal/tenants/{tenantId}/dialogs",
            request,
            cancellationToken);

    /// <summary>Диалог с сообщениями и карточкой гейта.</summary>
    public Task<DialogDto> GetDialogAsync(
        Guid tenantId,
        Guid sessionId,
        CancellationToken cancellationToken = default)
        => GetAsync<DialogDto>(
            $"api/v1/internal/tenants/{tenantId}/dialogs/{sessionId}",
            cancellationToken);

    /// <summary>Продолжает диалог сообщением.</summary>
    public Task<DialogDto> SendMessageAsync(
        Guid tenantId,
        Guid sessionId,
        string message,
        CancellationToken cancellationToken = default)
        => SendAsync<DialogDto>(
            HttpMethod.Post,
            $"api/v1/internal/tenants/{tenantId}/dialogs/{sessionId}/messages",
            new DialogMessageRequestDto(message),
            cancellationToken);

    /// <summary>Подтверждает/отклоняет ожидающий гейт через Controller → ResumeAsync.</summary>
    public Task<DialogDto> DecideAsync(
        Guid tenantId,
        Guid sessionId,
        Guid approvalId,
        ApprovalDecisionDto decision,
        CancellationToken cancellationToken = default)
        => SendAsync<DialogDto>(
            HttpMethod.Post,
            $"api/v1/internal/tenants/{tenantId}/dialogs/{sessionId}/approvals/{approvalId}/decision",
            decision,
            cancellationToken);

    private Task<T> GetAsync<T>(string url, CancellationToken cancellationToken)
        => SendAsync<T>(HttpMethod.Get, url, body: null, cancellationToken);

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string url,
        object? body,
        CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(method, url, body, cancellationToken).ConfigureAwait(false);

        var payload = await response.Content
            .ReadFromJsonAsync<T>(JsonSerializerOptions.Web, cancellationToken)
            .ConfigureAwait(false);

        return payload ?? throw new CabinetApiException(
            (int)response.StatusCode,
            "Controller API returned an empty response.");
    }

    private async Task SendAsync(
        HttpMethod method,
        string url,
        object? body,
        CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(method, url, body, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpMethod method,
        string url,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonSerializerOptions.Web);
        }

        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var status = (int)response.StatusCode;
        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.Dispose();

        throw new CabinetApiException(status, ExtractMessage(raw), raw);
    }

    private static string ExtractMessage(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "Controller API request failed.";
        }

        try
        {
            using var document = JsonDocument.Parse(raw);

            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (document.RootElement.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString() ?? raw;
                }

                if (document.RootElement.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString() ?? raw;
                }
            }
        }
        catch (JsonException)
        {
            // Не JSON — показываем как есть.
        }

        return raw;
    }
}
