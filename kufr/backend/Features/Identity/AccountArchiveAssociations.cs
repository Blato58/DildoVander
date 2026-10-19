using System.Security.Claims;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Keepsakes;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Identity;

internal static class AccountArchiveAssociations
{
    public static async Task<string> IssueAsync(HttpContext context, string targetAccountId, KeepsakeAccountProof proof)
    {
        var db = context.RequestServices.GetService<AccountDatabase>();
        if (db is null || string.IsNullOrWhiteSpace(targetAccountId) || targetAccountId.Length > 128
            || !await db.Users.AnyAsync(x => x.Id == targetAccountId && !x.Disabled))
        {
            throw new KeepsakeException("account_association_unavailable", 400);
        }
        await db.ArchiveTickets.Where(x => x.ExpiresAt <= AccountEndpoints.Now() || x.Consumed).ExecuteDeleteAsync();
        if (await db.ArchiveTickets.CountAsync(x => x.TargetUserId == targetAccountId) >= 20)
        {
            throw new KeepsakeException("account_association_unavailable", 429);
        }
        var token = AccountEndpoints.Secret();
        db.ArchiveTickets.Add(new AccountArchiveTicket
        {
            TokenHash = AccountEndpoints.Hash(token), TargetUserId = targetAccountId,
            Locator = proof.Locator, Epoch = proof.Epoch, ExpiresAt = AccountEndpoints.Now() + 300
        });
        await db.SaveChangesAsync();
        return token;
    }

    public static void MapAccountArchives(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/app/v1/archive/associations").RequireAuthorization().RequireRateLimiting("account");
        group.AddEndpointFilter(async (invocation, next) =>
        {
            try { return await next(invocation); }
            catch (KeepsakeException error)
            {
                return ApiProblem.Create(invocation.HttpContext, error.Status is 401 or 403 or 404 ? 404 : error.Status, "archive_association_unavailable");
            }
        });
        group.MapPost("", async (AccountArchiveClaim request, HttpContext context, AccountDatabase db, KeepsakeStore store) =>
        {
            if (request.Ticket is not { Length: >= 32 and <= 128 }) { return ApiProblem.Create(context, 400, "invalid_archive_association"); }
            var hash = AccountEndpoints.Hash(request.Ticket);
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var ticket = await db.ArchiveTickets.AsNoTracking().SingleOrDefaultAsync(x => x.TokenHash == hash && x.TargetUserId == userId);
            if (ticket is null || ticket.Consumed || ticket.ExpiresAt <= AccountEndpoints.Now()
                || !store.IsAccountAssociationCurrent(ticket.Locator, ticket.Epoch))
            { return ApiProblem.Create(context, 404, "archive_association_unavailable"); }
            await using var transaction = await db.Database.BeginTransactionAsync();
            if (await db.ArchiveTickets.Where(x => x.TokenHash == hash && !x.Consumed && x.ExpiresAt > AccountEndpoints.Now())
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.Consumed, true)) != 1)
            { return ApiProblem.Create(context, 404, "archive_association_unavailable"); }
            var link = await db.ArchiveAssociations.SingleOrDefaultAsync(x => x.UserId == userId && x.Locator == ticket.Locator);
            if (link is null)
            {
                if (await db.ArchiveAssociations.CountAsync(x => x.UserId == userId) >= 100) { return ApiProblem.Create(context, 409, "archive_association_limit"); }
                link = new AccountArchiveAssociation { UserId = userId, Locator = ticket.Locator };
                db.ArchiveAssociations.Add(link);
            }
            link.Epoch = ticket.Epoch; link.LinkedAt = AccountEndpoints.Now();
            await db.SaveChangesAsync(); await transaction.CommitAsync();
            return Results.Ok(new { link.Locator, link.LinkedAt });
        });
        group.MapGet("", async (HttpContext context, AccountDatabase db, KeepsakeStore store) =>
        {
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var links = await db.ArchiveAssociations.Where(x => x.UserId == userId).OrderByDescending(x => x.LinkedAt).Take(100).ToArrayAsync();
            var items = new List<object>();
            foreach (var link in links)
            {
                if (!store.IsAccountAssociationCurrent(link.Locator, link.Epoch)) { continue; }
                try
                {
                    var view = store.ReadForAccount(link.Locator, link.Epoch);
                    items.Add(new
                    {
                        link.Locator, link.LinkedAt, view.Snapshot.OfficialName,
                        storyTitle = view.Editions.OrderByDescending(x => x.CreatedAt).FirstOrDefault()?.Story.Title,
                        hasPortrait = view.Portraits.Count > 0
                    });
                }
                catch (KeepsakeException error) when (error.Status == 404) { /* Revoked between checks. */ }
            }
            return Results.Ok(new { items });
        });
        group.MapGet("/{locator:guid}", async (string locator, HttpContext context, AccountDatabase db, KeepsakeStore store) =>
        {
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var link = await db.ArchiveAssociations.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId && x.Locator == locator);
            if (link is null || !store.IsAccountAssociationCurrent(locator, link.Epoch)) { return ApiProblem.Create(context, 404, "archive_association_unavailable"); }
            return Results.Json(KeepsakeEndpoints.ProjectForAccount(store.ReadForAccount(locator, link.Epoch)));
        });
        group.MapGet("/{locator:guid}/assets/{assetId:guid}", async (string locator, string assetId, HttpContext context, AccountDatabase db, KeepsakeStore store) =>
        {
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var link = await db.ArchiveAssociations.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId && x.Locator == locator);
            if (link is null || !store.IsAccountAssociationCurrent(locator, link.Epoch)) { return ApiProblem.Create(context, 404, "archive_association_unavailable"); }
            var asset = store.ReadAssetForAccount(locator, link.Epoch, assetId);
            return Results.File(asset.Bytes, asset.Asset.MediaType);
        });
        group.MapDelete("/{locator:guid}", async (string locator, HttpContext context, AccountDatabase db) =>
        {
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            await db.ArchiveAssociations.Where(x => x.UserId == userId && x.Locator == locator).ExecuteDeleteAsync();
            return Results.NoContent();
        });
    }
    private sealed record AccountArchiveClaim(string Ticket);
}
