using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Keepsakes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Identity;

internal static class AccountCollectionEndpoints
{
    private const int MaxLimit = 50;
    private const int CandidateWindow = 200;
    private const int MaxCursorLength = 2048;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] AccessKinds = ["all", "owned", "linked"];

    internal static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/app/v1/archive/collection").RequireAuthorization().RequireRateLimiting("account");
        group.MapGet("", ReadAsync);
    }

    private static async Task<IResult> ReadAsync(HttpContext context, AccountDatabase db, KeepsakeStore store,
        IDataProtectionProvider protection, CancellationToken cancellationToken)
    {
        try
        {
            if (!TrySingleQuery(context.Request, "limit", out var limitText)
                || !TrySingleQuery(context.Request, "cursor", out var token)
                || !TrySingleQuery(context.Request, "q", out var query)
                || !TrySingleQuery(context.Request, "access", out var access))
            {
                return BadRequest(context);
            }
            var limit = 25;
            if (limitText is not null && (!int.TryParse(limitText, out limit) || limit is < 1 or > MaxLimit))
            {
                return BadRequest(context);
            }
            query = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
            if (query is { Length: > 80 }) { return BadRequest(context); }
            access = string.IsNullOrEmpty(access) ? "all" : access;
            if (!AccessKinds.Contains(access, StringComparer.Ordinal)) { return BadRequest(context); }
            if (token is { Length: > MaxCursorLength }) { return BadRequest(context); }

            var actor = AccountOwnedArchives.Actor(context);
            var accountId = actor.AccountId ?? throw new KeepsakeException("account_required", 401);
            store.ValidateSession(actor);

            var protector = protection.CreateProtector("DV10.AccountArchiveCollection.Cursor.v1");
            AccountCollectionCursor? cursor = null;
            if (token is not null)
            {
                try
                {
                    var plaintext = protector.Unprotect(token);
                    cursor = JsonSerializer.Deserialize<AccountCollectionCursor>(plaintext, Json);
                }
                catch (CryptographicException) { return BadRequest(context); }
                catch (JsonException) { return BadRequest(context); }
                if (cursor is null || cursor.SchemaVersion != 1 || cursor.AccountId != accountId
                    || cursor.Limit != limit || cursor.Query != query || cursor.Access != access
                    || cursor.OwnedWatermark < 0 || cursor.LinkedWatermark < 0
                    || !Guid.TryParseExact(cursor.BeforeLocator, "D", out _))
                {
                    return BadRequest(context);
                }
            }

            var ownedWatermark = cursor?.OwnedWatermark ?? store.CustomerOwnedCollectionRevision(accountId);
            var linkedWatermark = cursor?.LinkedWatermark ?? await db.ArchiveCollectionStates.AsNoTracking()
                .Where(state => state.Id == 1).Select(state => state.Revision).SingleOrDefaultAsync(cancellationToken);
            var beforeLinkedAt = cursor?.BeforeLinkedAt;
            var beforeLocator = cursor?.BeforeLocator;

            var ownedRows = access == "linked" ? [] : store.CustomerOwnedCollectionCandidates(accountId,
                ownedWatermark, beforeLinkedAt, beforeLocator, CandidateWindow + 1);
            var explicitRows = access == "owned" ? [] : await db.ArchiveAssociations.AsNoTracking()
                .Where(link => link.UserId == accountId && link.CollectionRevision <= linkedWatermark
                    && (beforeLinkedAt == null || link.LinkedAt < beforeLinkedAt
                        || (link.LinkedAt == beforeLinkedAt && link.Locator.CompareTo(beforeLocator!) > 0)))
                .OrderByDescending(link => link.LinkedAt).ThenBy(link => link.Locator)
                .Take(CandidateWindow + 1).ToArrayAsync(cancellationToken);

            var explicitLocators = explicitRows.Select(link => link.Locator).ToArray();
            var ownedLocators = store.CurrentCustomerOwnerLocators(accountId, explicitLocators);
            var candidates = new List<AccountCollectionCandidate>(ownedRows.Length + explicitRows.Length);
            candidates.AddRange(ownedRows.Select(link => new AccountCollectionCandidate(link.Locator,
                link.LinkedAt, true, link.Epoch, link.SessionId, link.CollectionRevision)));
            // Coalesce duplicates before applying the bounded scan window. A
            // current immutable owner always wins, including when its owner
            // revision is newer than the cursor watermark; the explicit grant
            // must never become a fallback owner row.
            candidates.AddRange(explicitRows.Where(link => !ownedLocators.Contains(link.Locator))
                .Select(link => new AccountCollectionCandidate(link.Locator,
                    link.LinkedAt, false, link.Epoch, null, link.CollectionRevision)));
            candidates.Sort(static (left, right) =>
            {
                var linkedAt = right.LinkedAt.CompareTo(left.LinkedAt);
                return linkedAt != 0 ? linkedAt : StringComparer.Ordinal.Compare(left.Locator, right.Locator);
            });

            var items = new List<AccountCollectionItem>(limit);
            var scanned = 0;
            AccountCollectionCandidate? lastScanned = null;
            foreach (var candidate in candidates)
            {
                if (scanned == CandidateWindow || items.Count == limit) { break; }
                scanned++;
                lastScanned = candidate;
                cancellationToken.ThrowIfCancellationRequested();
                AccountCollectionItem? item;
                try
                {
                    item = await ProjectAsync(candidate, accountId, ownedWatermark, linkedWatermark,
                        context, db, store, cancellationToken);
                }
                catch (KeepsakeException error) when (error.Status == 404)
                {
                    // One source can be revoked between the bounded candidate
                    // query and its private projection. Omit that row only.
                    continue;
                }
                if (item is null || !Matches(item, query)) { continue; }
                items.Add(item);
            }

            store.ValidateSession(actor);
            var hasMore = candidates.Count > scanned || ownedRows.Length == CandidateWindow + 1
                || explicitRows.Length == CandidateWindow + 1;
            var nextCursor = hasMore && lastScanned is not null
                ? ProtectCursor(protector, new AccountCollectionCursor(1, accountId, limit, query, access,
                    ownedWatermark, linkedWatermark, lastScanned.LinkedAt, lastScanned.Locator))
                : null;
            if (nextCursor is { Length: > MaxCursorLength }) { return ApiProblem.Create(context, 500, "archive_collection_unavailable"); }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.Json(new AccountCollectionPage(1, items, nextCursor));
        }
        catch (KeepsakeException error)
        {
            var status = error.Status is 401 or 403 or 404 ? 404 : error.Status;
            return ApiProblem.Create(context, status, "archive_association_unavailable");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
    }

    private static async Task<AccountCollectionItem?> ProjectAsync(AccountCollectionCandidate candidate,
        string accountId, long ownedWatermark, long linkedWatermark, HttpContext context, AccountDatabase db,
        KeepsakeStore store, CancellationToken cancellationToken)
    {
        if (candidate.OriginalOwner)
        {
            var current = store.CustomerAccountLink(accountId, candidate.Locator);
            if (current is null || current.CollectionRevision != candidate.SourceRevision
                || current.CollectionRevision > ownedWatermark || current.Epoch != candidate.Epoch
                || current.LinkedAt != candidate.LinkedAt)
            {
                return null;
            }
            var metadata = store.ReadCustomerCollectionMetadata(candidate.Locator, AccountOwnedArchives.Actor(context));
            var (characterName, eveningAt) = await CapturedCharacterAsync(candidate,
                accountId, context, cancellationToken);
            var after = store.CustomerAccountLink(accountId, candidate.Locator);
            if (after is null || after.CollectionRevision != candidate.SourceRevision
                || after.CollectionRevision > ownedWatermark || after.Epoch != candidate.Epoch
                || after.LinkedAt != candidate.LinkedAt)
            {
                return null;
            }
            store.ValidateSession(AccountOwnedArchives.Actor(context));
            var item = new AccountCollectionItem(candidate.Locator, candidate.LinkedAt,
                EmptyToNull(metadata.OfficialName), EmptyToNull(metadata.StoryTitle), metadata.HasPortrait,
                "originalOwner", true, true, false, metadata.Readiness, characterName, eveningAt);
            return item;
        }

        var link = await db.ArchiveAssociations.AsNoTracking().SingleOrDefaultAsync(value =>
            value.UserId == accountId && value.Locator == candidate.Locator, cancellationToken);
        if (link is null || link.CollectionRevision != candidate.SourceRevision
            || link.CollectionRevision > linkedWatermark || link.Epoch != candidate.Epoch
            || link.LinkedAt != candidate.LinkedAt || store.CustomerAccountLink(accountId, candidate.Locator) is not null
            || !store.IsAccountAssociationCurrent(candidate.Locator, link.Epoch))
        {
            return null;
        }
        var linkedMetadata = store.ReadLinkedCollectionMetadata(candidate.Locator, link.Epoch);
        var afterLink = await db.ArchiveAssociations.AsNoTracking().SingleOrDefaultAsync(value =>
            value.UserId == accountId && value.Locator == candidate.Locator, cancellationToken);
        if (afterLink is null || afterLink.CollectionRevision != candidate.SourceRevision
            || afterLink.CollectionRevision > linkedWatermark || afterLink.Epoch != candidate.Epoch
            || store.CustomerAccountLink(accountId, candidate.Locator) is not null
            || !store.IsAccountAssociationCurrent(candidate.Locator, afterLink.Epoch))
        {
            return null;
        }
        return new AccountCollectionItem(candidate.Locator, link.LinkedAt,
            EmptyToNull(linkedMetadata.OfficialName), EmptyToNull(linkedMetadata.StoryTitle), linkedMetadata.HasPortrait,
            "linkedViewer", false, false, true, linkedMetadata.Readiness, null, null);
    }

    private static async Task<(string? Name, long? EveningAt)> CapturedCharacterAsync(
        AccountCollectionCandidate candidate, string accountId, HttpContext context, CancellationToken cancellationToken)
    {
        if (candidate.CustomerSessionId is null) { return (null, null); }
        var journal = context.RequestServices.GetService<OperationJournal>();
        var session = journal?.CustomerSession(candidate.CustomerSessionId, accountId);
        if (session is null || session.Snapshot.Length == 0) { return (null, null); }
        var profiles = context.RequestServices.GetService<CharacterProfileStore>();
        if (profiles is null || await profiles.IsDeletedAsync(accountId, session.ProfileId, cancellationToken))
        {
            return (null, session.CreatedAt);
        }
        try
        {
            var captured = JsonSerializer.Deserialize<CustomerConversationSnapshot>(session.Snapshot, Json);
            return (EmptyToNull(captured?.Profile.UserInput.Name), session.CreatedAt);
        }
        catch (JsonException) { return (null, session.CreatedAt); }
    }

    private static bool Matches(AccountCollectionItem item, string? query)
    {
        if (query is null) { return true; }
        return Contains(item.OfficialName, query) || Contains(item.StoryTitle, query)
            || Contains(item.CharacterName, query);
    }

    private static bool Contains(string? value, string query) => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string ProtectCursor(Microsoft.AspNetCore.DataProtection.IDataProtector protector,
        AccountCollectionCursor cursor) => protector.Protect(JsonSerializer.Serialize(cursor, Json));

    private static bool TrySingleQuery(HttpRequest request, string name, out string? value)
    {
        var values = request.Query[name];
        value = values.Count == 1 ? values[0] : null;
        return values.Count <= 1;
    }

    private static IResult BadRequest(HttpContext context) =>
        ApiProblem.Create(context, 400, "invalid_archive_collection_query");
}
