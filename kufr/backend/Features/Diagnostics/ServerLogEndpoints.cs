using System.Globalization;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;

namespace Kufr.Backend.Features.Diagnostics;

internal static class ServerLogEndpoints
{
    internal static IEndpointRouteBuilder MapServerLogs(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/admin/logs", (HttpContext context, LogReadAccess access, ServerLogStore logs) =>
        {
            if (!access.Authenticate(context)) return ApiProblem.Create(context, 401, "log_credential_required");
            var query = context.Request.Query;
            if (query.Any(pair => !new[] { "after", "limit", "level", "traceId", "operationId" }.Contains(pair.Key) || pair.Value.Count != 1))
                return ApiProblem.Create(context, 400, "invalid_log_query");
            long? after = null;
            if (query.ContainsKey("after"))
            {
                if (!long.TryParse(query["after"], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                    || value < 0 || value > 9_007_199_254_740_991) return ApiProblem.Create(context, 400, "invalid_log_query");
                after = value;
            }
            var limit = 200;
            if (query.ContainsKey("limit") && (!int.TryParse(query["limit"], out limit) || limit is < 1 or > ServerLogStore.MaxPageSize))
                return ApiProblem.Create(context, 400, "invalid_log_query");
            var level = query.ContainsKey("level") ? query["level"].ToString() : "info";
            if (level is not ("info" or "warning" or "error")) return ApiProblem.Create(context, 400, "invalid_log_query");
            string? Identity(string name) => query.ContainsKey(name) && Guid.TryParseExact(query[name], "D", out var id)
                ? id.ToString() : null;
            var trace = Identity("traceId"); var operation = Identity("operationId");
            if (query.ContainsKey("traceId") && trace is null || query.ContainsKey("operationId") && operation is null)
                return ApiProblem.Create(context, 400, "invalid_log_query");
            return Results.Ok(logs.Read(after, limit, level, trace, operation));
        });
        endpoints.MapGet("/v1/admin/logs/status", (HttpContext context, LogReadAccess access, ConversationStore conversations) =>
            access.Authenticate(context) ? Results.Ok(conversations.ReadServerModes())
                : ApiProblem.Create(context, 401, "log_credential_required"));
        return endpoints;
    }
}
