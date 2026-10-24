using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Keepsakes;

namespace Kufr.Backend.Features.Customers;

internal static class CustomerSessionResultEndpoints
{
    internal sealed record WandPublicationRequest(bool Approved, string EditionId, int Revision);
    internal static void MapCustomerSessionResult(this RouteGroupBuilder accounts)
    {
        accounts.MapPost("/keepsakes/{id:guid}/wand-artwork/retry", (string id, HttpContext http, KeepsakeStore keepsakes) =>
        {
            try
            {
                var key = http.Request.Headers["Idempotency-Key"].ToString();
                return Results.Ok(keepsakes.RetryWandArtwork(id, AccountOwnedArchives.Actor(http), key));
            }
            catch (KeepsakeException error) { return ApiProblem.Create(http, error.Status, error.Code); }
        });
        accounts.MapPost("/keepsakes/{id:guid}/wand-publication", (string id, WandPublicationRequest request, HttpContext http, KeepsakeStore keepsakes) =>
        {
            try
            {
                var view = keepsakes.SetWandPublication(id, AccountOwnedArchives.Actor(http), request.Approved, request.EditionId, request.Revision);
                return Results.Ok(KeepsakeEndpoints.Project(view, "originalOwner"));
            }
            catch (KeepsakeException error) { return ApiProblem.Create(http, error.Status, error.Code); }
        });
        accounts.MapGet("/duels", (HttpContext http, KeepsakeStore keepsakes) =>
        {
            var duels = http.RequestServices.GetService<Kufr.Backend.Features.Show.DuelStore>();
            if (duels is null) return ApiProblem.Create(http, 503, "duels_unavailable");
            var actor = AccountOwnedArchives.Actor(http);
            var identities = keepsakes.OwnedBattleIdentities(actor);
            return Results.Ok(new { items = duels.AccountHistory(identities).Select(keepsakes.ProjectBattleArtwork) });
        });
        accounts.MapGet("/duels/{id:guid}", (string id, HttpContext http, KeepsakeStore keepsakes) =>
        {
            var duels = http.RequestServices.GetService<Kufr.Backend.Features.Show.DuelStore>();
            if (duels is null) return ApiProblem.Create(http, 503, "duels_unavailable");
            var actor = AccountOwnedArchives.Actor(http);
            var identities = keepsakes.OwnedBattleIdentities(actor);
            var duel = duels.AccountHistory(identities, id).SingleOrDefault();
            return duel is null ? ApiProblem.Create(http, 404, "duel_unavailable") : Results.Ok(keepsakes.ProjectBattleArtwork(duel));
        });
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
                var result = keepsakes.ReadCustomerSessionResult(id, actor);
                var generated = http.RequestServices.GetService<Kufr.Backend.Features.Wands.WandGenerationStore>()
                    ?.Read(session.OwnerId, session.OperationId);
                return Results.Json(result with { WandGeneration = generated is null ? null
                    : new(generated.Status, generated.ErrorCode, generated.Status == "unavailable") });
            }
            catch (KeepsakeException error)
            {
                var status = error.Status is 401 or 403 or 404 ? 404 : error.Status;
                return ApiProblem.Create(http, status, "customer_session_unavailable");
            }
        });
    }
}
