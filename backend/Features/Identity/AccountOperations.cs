using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Diagnostics;
using Kufr.Backend.Features.Keepsakes;

namespace Kufr.Backend.Features.Identity;

internal static class AccountOperations
{
    public static void MapAccountOperations(this IEndpointRouteBuilder root)
    {
        var group = root.MapGroup("/admin/operations").RequireAuthorization();
        group.AddEndpointFilter((invocation, next) => AccountRoles.IsAdmin(invocation.HttpContext.User)
            ? next(invocation) : ValueTask.FromResult<object?>(ApiProblem.Create(invocation.HttpContext, 403, "account_admin_required")));
        group.MapGet("/status", (HttpContext context, IConfiguration config) => Results.Ok(new
        {
            modes = context.RequestServices.GetService<ConversationStore>()?.ReadServerModes(),
            readiness = new
            {
                accounts = config.GetValue<bool>("ApplicationIdentity:Enabled"),
                keepsakes = config.GetValue<bool>("Keepsake:Enabled"),
                keepsakeGeneration = config.GetValue<bool>("Keepsake:GenerationEnabled"),
                passkeyRelyingParty = config["ApplicationIdentity:RelyingPartyId"],
                nativeAssociationConfigured = !string.IsNullOrEmpty(config["ApplicationIdentity:AppleAppId"])
            },
            archive = context.RequestServices.GetService<KeepsakeStore>()?.AdminList(30)
        }));
        group.MapPut("/modes", (ServerModeChange request, HttpContext context) =>
        {
            var store = context.RequestServices.GetService<ConversationStore>();
            if (store is null) { return ApiProblem.Create(context, 503, "server_modes_unavailable"); }
            var result = store.ChangeServerModes(request);
            return result.Error is null ? Results.Ok(result.State) : ApiProblem.Create(context, result.Status, result.Error);
        });
        group.MapGet("/logs", (HttpContext context) =>
        {
            var query = context.Request.Query;
            if (query.Keys.Any(x => x is not ("after" or "level"))) { return ApiProblem.Create(context, 400, "invalid_log_query"); }
            long? after = null;
            if (query.TryGetValue("after", out var value))
            {
                if (!long.TryParse(value, out var parsed) || parsed < 0) { return ApiProblem.Create(context, 400, "invalid_log_query"); }
                after = parsed;
            }
            var level = query["level"].ToString();
            if (level.Length == 0) { level = "warning"; }
            if (level is not ("info" or "warning" or "error")) { return ApiProblem.Create(context, 400, "invalid_log_query"); }
            var logs = context.RequestServices.GetService<ServerLogStore>();
            return logs is null ? ApiProblem.Create(context, 503, "server_logs_unavailable") : Results.Ok(logs.Read(after, 100, level, null, null));
        });
        group.MapPost("/keepsakes/{id:guid}/jobs/{jobId:guid}/cancel", (string id, string jobId, OperationConfirmation request, HttpContext context) =>
        {
            if (!Guid.TryParseExact(request.OperationId, "D", out _)) { return ApiProblem.Create(context, 400, "invalid_idempotency_key"); }
            var store = context.RequestServices.GetService<KeepsakeStore>();
            if (store is null) { return ApiProblem.Create(context, 503, "keepsakes_unavailable"); }
            try { store.AdminCancel(id, jobId, request.OperationId); return Results.NoContent(); }
            catch (KeepsakeException error) { return ApiProblem.Create(context, error.Status, error.Code); }
        });
        group.MapPost("/keepsakes/{id:guid}/revoke-shares", (string id, OperationConfirmation request, HttpContext context) =>
        {
            if (!Guid.TryParseExact(request.OperationId, "D", out _)) { return ApiProblem.Create(context, 400, "invalid_idempotency_key"); }
            var store = context.RequestServices.GetService<KeepsakeStore>();
            if (store is null) { return ApiProblem.Create(context, 503, "keepsakes_unavailable"); }
            try { store.AdminRevokeShares(id, request.OperationId); return Results.NoContent(); }
            catch (KeepsakeException error) { return ApiProblem.Create(context, error.Status, error.Code); }
        });
    }
    private sealed record OperationConfirmation(string OperationId);
}
