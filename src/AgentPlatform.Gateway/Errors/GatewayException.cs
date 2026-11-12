namespace AgentPlatform.Gateway.Errors;

/// <summary>
/// Ожидаемая ошибка обработки запроса канала: код ошибки, HTTP-статус и
/// (для rate-limit) рекомендуемый интервал повторной попытки.
/// Endpoint-ы Gateway превращают её в ProblemDetails, не раскрывая внутренних
/// сообщений исключений наружу.
/// </summary>
public sealed class GatewayException : Exception
{
    private GatewayException(int statusCode, string code, string message, TimeSpan? retryAfter = null)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        RetryAfter = retryAfter;
    }

    /// <summary>HTTP-статус ответа.</summary>
    public int StatusCode { get; }

    /// <summary>Машиночитаемый код ошибки (title в ProblemDetails).</summary>
    public string Code { get; }

    /// <summary>Интервал до повторной попытки (для 429).</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Некорректный запрос (400).</summary>
    public static GatewayException BadRequest(string code, string message)
        => new(StatusCodes.Status400BadRequest, code, message);

    /// <summary>Не аутентифицирован / неверный JWT виджета (401).</summary>
    public static GatewayException Unauthorized(string message)
        => new(StatusCodes.Status401Unauthorized, "unauthorized", message);

    /// <summary>Доступ запрещён (403): чужой визитер, заблокированный тенант.</summary>
    public static GatewayException Forbidden(string code, string message)
        => new(StatusCodes.Status403Forbidden, code, message);

    /// <summary>Не найдено (404): тенант, сессия.</summary>
    public static GatewayException NotFound(string code, string message)
        => new(StatusCodes.Status404NotFound, code, message);

    /// <summary>Конфликт состояния (409): нет канала/агента, сессия ждёт гейт.</summary>
    public static GatewayException Conflict(string code, string message)
        => new(StatusCodes.Status409Conflict, code, message);

    /// <summary>Превышен лимит запросов тенанта (429).</summary>
    public static GatewayException TooManyRequests(string message, TimeSpan retryAfter)
        => new(StatusCodes.Status429TooManyRequests, "rate_limited", message, retryAfter);

    /// <summary>Сервис временно не может обработать запрос (503).</summary>
    public static GatewayException ServiceUnavailable(string code, string message)
        => new(StatusCodes.Status503ServiceUnavailable, code, message);
}
