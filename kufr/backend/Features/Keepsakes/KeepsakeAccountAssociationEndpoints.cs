using Kufr.Backend.Features.Identity;

namespace Kufr.Backend.Features.Keepsakes;

internal static partial class KeepsakeEndpoints
{
    private static void MapAccountAssociations(RouteGroupBuilder group)
    {
        group.MapPost("/keepsakes/{id:guid}/account-associations", async (HttpContext context, string id, KeepsakeStore store) =>
        {
            if (ShareOnly(context)) { return Error(404, "access_unavailable"); }
            var request = await BodyAsync<AccountAssociationRequest>(context);
            if (string.IsNullOrWhiteSpace(request.TargetAccountId) || request.TargetAccountId.Length > 128)
            {
                return Error(400, "invalid_request");
            }
            var proof = store.AuthorizeAccountAssociation(id, Session(context));
            var ticket = store.RecallAccountAssociationTicket(proof, Session(context), request.TargetAccountId, Key(context));
            if (ticket is null)
            {
                ticket = await AccountArchiveAssociations.IssueAsync(context, request.TargetAccountId, proof);
                ticket = store.RememberAccountAssociationTicket(proof, Session(context), request.TargetAccountId, Key(context), ticket);
            }
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { schemaVersion = 1, ticket });
        });
    }

    internal static object ProjectForAccount(KeepsakeView view) => new
    {
        schemaVersion = 1, locator = view.Id, view.Revision, view.RetentionMode, state = view.State,
        readOnly = true, accountLinked = true, sharedEditionId = (string?)null,
        view.WizardIdentities, prophecies = (object?)null,
        snapshot = new { view.Snapshot.OfficialName, view.Snapshot.CatalogId, view.Snapshot.CatalogVersion, view.Snapshot.Slot },
        editions = view.Editions.Select(edition => new { edition.Id, edition.Story, edition.ArtworkAssetId, edition.Synthetic, edition.PublicationState, edition.CreatedAt }),
        portraits = view.Portraits.Select(portrait => new { portrait.Id, portrait.EditionId, portrait.AssetId, portrait.Synthetic, portrait.CreatedAt }),
        uploads = Array.Empty<object>(), jobs = Array.Empty<object>(),
        assets = view.Assets.Select(asset => new { asset.Id, asset.Kind, asset.MediaType, asset.Length, asset.EditionId, asset.CreatedAt })
    };

    private sealed record AccountAssociationRequest(int SchemaVersion, string TargetAccountId) : VersionRequest(SchemaVersion);
}
