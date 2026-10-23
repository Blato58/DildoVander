using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Keepsakes;

namespace Kufr.Backend.Features.Customers;

internal static class CustomerSessionResultEndpoints
{
    internal static void MapCustomerSessionResult(this RouteGroupBuilder accounts)
    {
        accounts.MapGet("/sessions/{id:guid}/result", (string id, HttpContext http,
            OperationJournal journal, KeepsakeStore keepsakes) =>
        {
            try
            {
                var actor = AccountOwnedArchives.Actor(http);
                keepsakes.ValidateSession(actor);
                var session = journal.CustomerSession(id, actor.AccountId);
                if (session is null) { return ApiProblem.Create(http, 404, "customer_session_unavailable"); }
                if (session.Snapshot.Length == 0)
                {
                    return Results.Json(keepsakes.UnavailableCustomerSessionResult(id));
                }
                return Results.Json(keepsakes.ReadCustomerSessionResult(id, actor));
            }
            catch (KeepsakeException error)
            {
                var status = error.Status is 401 or 403 or 404 ? 404 : error.Status;
                return ApiProblem.Create(http, status, "customer_session_unavailable");
            }
        });
    }
}
