using System.Diagnostics;
using Microsoft.AspNetCore.Routing;

namespace Kufr.Backend.Features.Diagnostics;

internal static class ServerLogEvents
{
    internal static async Task RequestAsync(HttpContext context, RequestDelegate next, ServerLogStore logs)
    {
        context.TraceIdentifier = Guid.NewGuid().ToString();
        context.Response.Headers["X-Request-Id"] = context.TraceIdentifier;
        var start = Stopwatch.GetTimestamp();
        string? failure = null;
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        { failure = "client_disconnected"; throw; }
        catch (Exception) { failure = "unhandled_error"; throw; }
        finally
        {
            var route = Route(context);
            var status = failure == "unhandled_error" ? 500
                : context.RequestAborted.IsCancellationRequested ? 499 : context.Response.StatusCode;
            // Polling/PCM uploads and the log reader itself would overwhelm the
            // useful history. Their failures remain visible.
            var quiet = context.Request.Method == "GET" || route.EndsWith("/{streamId}/audio", StringComparison.Ordinal);
            if (status >= 400 || !quiet)
                logs.Write(status >= 500 ? "error" : status >= 400 ? "warning" : "info",
                    "http_request", context.TraceIdentifier, route, Method(context), status,
                    Math.Round(Stopwatch.GetElapsedTime(start).TotalMilliseconds, 1),
                    Operation(context), Conversation(context), failure ?? context.Items["diagnosticCode"] as string);
        }
    }

    internal static void Stream(HttpContext context, object frame)
    {
        // Read only the named scalar properties of our own stream envelopes.
        // Never inspect delta, text, result, correlation or provider exceptions.
        var type = frame.GetType();
        var name = type.GetProperty("type")?.GetValue(frame) as string;
        if (name is not ("started" or "completed" or "listening" or "speech_stopped" or "failed" or "closed")) return;
        var operation = Guid.TryParse(type.GetProperty("operationId")?.GetValue(frame)?.ToString(), out var id) ? id.ToString() : null;
        var code = type.GetProperty("code")?.GetValue(frame) as string;
        context.RequestServices.GetService<ServerLogStore>()?.Write(name == "failed" ? "warning" : "info",
            "realtime_" + name, context.TraceIdentifier, Route(context), Method(context),
            operationId: operation, conversationId: Conversation(context), code: code);
    }

    private static string Route(HttpContext context) =>
        ((context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(unmatched)")
            .Replace("{streamId:guid}", "{streamId}", StringComparison.Ordinal);
    private static string Method(HttpContext context) => context.Request.Method is "GET" or "POST" or "PUT" or "DELETE" or "OPTIONS"
        ? context.Request.Method : "OTHER";
    private static string? Identity(HttpContext context) => Guid.TryParse(context.Request.RouteValues["id"]?.ToString(), out var id) ? id.ToString() : null;
    private static string? Operation(HttpContext context) => Guid.TryParse(context.Items["diagnosticOperationId"]?.ToString(), out var id)
        ? id.ToString() : context.Request.Path.StartsWithSegments("/v1/operations") ? Identity(context) : null;
    private static string? Conversation(HttpContext context) => context.Request.Path.StartsWithSegments("/v1/conversations") ? Identity(context) : null;
}
