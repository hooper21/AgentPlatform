using System.Globalization;
using AgentPlatform.Core.Json;
using AgentPlatform.Gateway.Contracts;
using AgentPlatform.Gateway.Errors;
using AgentPlatform.Gateway.Security;
using AgentPlatform.Gateway.Sessions;
using AgentPlatform.Infrastructure.Tenancy;

namespace AgentPlatform.Gateway.Endpoints;

/// <summary>
/// Endpoint-ы канала (виджет):
/// <list type="bullet">
/// <item>POST /api/sessions/{tenantKey}/messages — приём сообщения (JWT виджета);</item>
/// <item>GET /api/sessions/{sessionId}/stream — SSE-стриминг ответа агента.</item>
/// </list>
/// Ошибки ожидаемого вида отдаются как ProblemDetails; внутренние сообщения
/// исключений наружу не попадают.
/// </summary>
public static class GatewayEndpoints
{
    /// <summary>Регистрирует endpoint-ы Gateway.</summary>
    public static IEndpointRouteBuilder MapGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/sessions/{tenantKey}/messages", AcceptMessageAsync);
        app.MapGet("/api/sessions/{sessionId:guid}/stream", StreamReplyAsync);

        return app;
    }

    /// <summary>Приём сообщения из канала: ход агента выполняется до завершения.</summary>
    private static async Task<IResult> AcceptMessageAsync(
        string tenantKey,
        IncomingChannelMessage message,
        HttpContext http,
        GatewayMessagePipeline pipeline,
        CancellationToken cancellationToken)
    {
        try
        {
            var turn = await pipeline
                .AcceptAsync(tenantKey, ChannelRequest.From(http.Request), message, cancellationToken)
                .ConfigureAwait(false);

            if (turn.VisitorCookie is { } cookieValue)
            {
                // Идентификация визитера: cookie выдаётся при первом визите.
                http.Response.Cookies.Append(
                    VisitorIdentityResolver.CookieName,
                    cookieValue,
                    VisitorIdentityResolver.CreateCookieOptions(http.Request.IsHttps));
            }

            return Results.Ok(turn.Response);
        }
        catch (GatewayException exception)
        {
            ApplyRetryAfter(http, exception);

            return Results.Problem(
                title: exception.Code,
                detail: exception.Message,
                statusCode: exception.StatusCode);
        }
    }

    /// <summary>
    /// SSE-поток ответа сессии. Токен виджета обязателен (EventSource не умеет
    /// передавать заголовки, поэтому принимается ?access_token): сессия должна
    /// принадлежать тенанту токена.
    /// </summary>
    private static async Task StreamReplyAsync(
        Guid sessionId,
        HttpContext http,
        WidgetJwtAuthenticator authenticator,
        TenantContext tenantContext,
        SessionBinder sessions,
        ReplyStreamHub streamHub,
        CancellationToken cancellationToken)
    {
        try
        {
            var identity = authenticator.Authenticate(ChannelRequest.From(http.Request).WidgetToken);
            tenantContext.SetTenant(identity.TenantId);

            var session = await sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false);

            if (session is null)
            {
                await WriteProblemAsync(
                        http,
                        GatewayException.NotFound("session_not_found", $"Session {sessionId} is not found for this tenant."))
                    .ConfigureAwait(false);

                return;
            }
        }
        catch (GatewayException exception)
        {
            await WriteProblemAsync(http, exception).ConfigureAwait(false);
            return;
        }

        // HTTP/1.1 + flush после каждого события: виджет получает чанки сразу.
        // TODO(3.x) contract: heartbeat-комментарий для прокси с idle-timeout.
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers.Connection = "keep-alive";
        http.Response.Headers["X-Accel-Buffering"] = "no";

        await http.Response.StartAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await foreach (var streamEvent in streamHub
                .ReadAsync(sessionId, cancellationToken)
                .ConfigureAwait(false))
            {
                var json = PlatformJson.SerializeApi(streamEvent);

                await http.Response
                    .WriteAsync($"event: {streamEvent.Type}\ndata: {json}\n\n", cancellationToken)
                    .ConfigureAwait(false);

                await http.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Клиент закрыл вкладку — это штатное завершение подписки.
        }
    }

    private static async Task WriteProblemAsync(HttpContext http, GatewayException exception)
    {
        ApplyRetryAfter(http, exception);

        await Results
            .Problem(title: exception.Code, detail: exception.Message, statusCode: exception.StatusCode)
            .ExecuteAsync(http)
            .ConfigureAwait(false);
    }

    private static void ApplyRetryAfter(HttpContext http, GatewayException exception)
    {
        if (exception.RetryAfter is { } retryAfter)
        {
            http.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }
    }
}
