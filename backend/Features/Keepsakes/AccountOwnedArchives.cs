using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kufr.Backend.Features.Access;
using Microsoft.AspNetCore.Identity;
using Kufr.Backend.Features.Identity;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Keepsakes;

internal static class AccountOwnedArchives
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    internal static KeepsakeActor Actor(HttpContext context)
    {
        var accountId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new KeepsakeException("account_required", 401);
        var sessionId = context.User.FindFirstValue(AccountIdentity.SessionClaim)
            ?? throw new KeepsakeException("account_required", 401);
        var users = context.RequestServices.GetRequiredService<UserManager<AccountUser>>();
        var securityStamp = context.User.FindFirstValue(users.Options.ClaimsIdentity.SecurityStampClaimType);
        if (string.IsNullOrEmpty(securityStamp)) { throw new KeepsakeException("invalid_session", 401); }

        var scopes = context.RequestServices.GetRequiredService<IServiceScopeFactory>();
        return KeepsakeActor.ForAccount(accountId, sessionId, () =>
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            var now = AccountEndpoints.Now();
            var session = db.Sessions.AsNoTracking().SingleOrDefault(item =>
                item.Id == sessionId && item.UserId == accountId && item.ExpiresAt > now);
            var user = db.Users.AsNoTracking().SingleOrDefault(item => item.Id == accountId);
            return session is not null && user is { Disabled: false } && user.SecurityStamp == securityStamp;
        });
    }

    internal static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/app/v1/archive/owned").RequireAuthorization().RequireRateLimiting("account");
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            if (!context.RequestServices.GetRequiredService<IConfiguration>().GetValue<bool>("Keepsake:Enabled"))
            {
                return ApiProblem.Create(context, 404, "archive_association_unavailable");
            }
            try
            {
                if (context.Request.Method is not ("GET" or "HEAD" or "OPTIONS")
                    && !Guid.TryParseExact(context.Request.Headers["Idempotency-Key"], "D", out _))
                {
                    return Error(context, 400, "invalid_idempotency_key");
                }
                context.Request.RouteValues.TryGetValue("id", out var routeId);
                var resourceId = routeId as string;
                var normalizedPath = (context.Request.Path.Value ?? "").TrimEnd('/');
                var deletionPath = resourceId is null ? "" : "/app/v1/archive/owned/" + resourceId;
                var isDeletionReplay = context.Request.Method == "DELETE" && resourceId is not null
                    && (normalizedPath.Equals(deletionPath, StringComparison.OrdinalIgnoreCase)
                        || normalizedPath.Equals(deletionPath + "/portrait", StringComparison.OrdinalIgnoreCase));
                if (context.Request.Method is not ("GET" or "HEAD" or "OPTIONS")
                    && resourceId is not null && !isDeletionReplay
                    && !context.RequestServices.GetRequiredService<KeepsakeStore>().Authorize(
                        resourceId, Actor(context), KeepsakeScope.Write))
                {
                    return ApiProblem.Create(context, 404, "archive_association_unavailable");
                }
                return await next(invocation);
            }
            catch (KeepsakeException error)
            {
                return error.Status is 401 or 403 or 404
                    ? ApiProblem.Create(context, 404, "archive_association_unavailable")
                    : Error(context, error.Status, error.Code);
            }
            catch (JsonException) { return Error(context, 400, "invalid_request"); }
            catch (BadHttpRequestException error) when (error.StatusCode == 413)
            { return Error(context, 413, "request_too_large"); }
        });

        group.MapGet("/{id:guid}", (HttpContext context, string id, KeepsakeStore store) =>
            Results.Json(KeepsakeEndpoints.ProjectForAccount(store.Read(id, Actor(context)), originalOwner: true)));
        group.MapGet("/{id:guid}/assets/{assetId:guid}", (HttpContext context, string id,
            string assetId, KeepsakeStore store) =>
        {
            var content = store.ReadAsset(id, Actor(context), assetId);
            context.Response.Headers.ContentDisposition = "attachment; filename=\"dildovander-" + content.Asset.Kind + Extension(content.Asset.MediaType) + "\"";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(content.Bytes, content.Asset.MediaType, enableRangeProcessing: false);
        });
        group.MapPost("/{id:guid}/wizard-identity", async (HttpContext context, string id, KeepsakeStore store) =>
        {
            var request = await BodyAsync<EditionRequest>(context);
            return Results.Json(store.CreateLegacyWizardIdentity(id, Actor(context), request.EditionId));
        });
        group.MapPost("/{id:guid}/account-associations", async (HttpContext context, string id, KeepsakeStore store) =>
        {
            var request = await BodyAsync<AccountAssociationRequest>(context);
            if (string.IsNullOrWhiteSpace(request.TargetAccountId) || request.TargetAccountId.Length > 128)
            {
                return Error(context, 400, "invalid_request");
            }
            var actor = Actor(context);
            var proof = store.AuthorizeAccountAssociation(id, actor);
            var ticket = store.RecallAccountAssociationTicket(proof, actor, request.TargetAccountId, Key(context));
            if (ticket is null)
            {
                ticket = await AccountArchiveAssociations.IssueAsync(context, request.TargetAccountId, proof);
                ticket = store.RememberAccountAssociationTicket(proof, actor, request.TargetAccountId, Key(context), ticket);
            }
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { schemaVersion = 1, ticket });
        });
        group.MapPost("/{id:guid}/generation", async (HttpContext context, string id, KeepsakeStore store) =>
        {
            var request = await BodyAsync<BriefRequest>(context);
            var job = store.EnqueueBase(id, Actor(context), request.Brief, Key(context));
            return Results.Json(new { schemaVersion = 1, job }, statusCode: 202);
        });
        group.MapPost("/{id:guid}/recovery-links", async (HttpContext context, string id,
            KeepsakeStore store, IConfiguration config) =>
        {
            await BodyAsync<EmptyRequest>(context);
            return Link(store.CreateRecovery(id, Actor(context), Key(context)), "r", config);
        });
        group.MapPost("/{id:guid}/share-links", async (HttpContext context, string id,
            KeepsakeStore store, IConfiguration config) =>
        {
            var request = await BodyAsync<EditionRequest>(context);
            var actor = Actor(context);
            var edition = store.Read(id, actor).Editions.SingleOrDefault(item => item.Id == request.EditionId);
            if (edition is null || string.IsNullOrEmpty(edition.ArtworkAssetId)) { return Error(context, 409, "edition_not_ready"); }
            foreach (var format in new[] { "story", "card", "zip" })
            {
                store.CreateExport(id, actor, request.EditionId, format, null, Key(context) + "/share-" + format);
            }
            return Link(store.CreateShare(id, actor, request.EditionId, Key(context)), "s", config);
        });
        group.MapDelete("/{id:guid}/recovery-links", async (HttpContext context, string id, KeepsakeStore store) =>
        {
            await BodyAsync<EmptyRequest>(context); store.RevokeRecovery(id, Actor(context)); return Results.NoContent();
        });
        group.MapDelete("/{id:guid}/share-links", async (HttpContext context, string id, KeepsakeStore store) =>
        {
            await BodyAsync<EmptyRequest>(context); store.RevokeShares(id, Actor(context)); return Results.NoContent();
        });
        group.MapPost("/{id:guid}/revoke-access", async (HttpContext context, string id, KeepsakeStore store) =>
        {
            var request = await BodyAsync<DeleteRequest>(context);
            var actor = Actor(context);
            if (request.Confirm != "delete" || request.ExpectedRevision != store.Read(id, actor).Revision)
            { return Error(context, 409, "confirmation_required"); }
            store.RevokeAllAccess(id, actor);
            return Results.NoContent();
        });
        group.MapPost("/{id:guid}/portrait-consents", async (HttpContext context, string id, KeepsakeStore store) =>
        {
            var request = await BodyAsync<ConsentRequest>(context);
            var version = store.SetPortraitConsent(id, Actor(context), request.Accepted, request.PolicyVersion, Key(context));
            return Results.Json(new { schemaVersion = 1, consentVersion = version });
        });
        group.MapPost("/{id:guid}/portrait-uploads", async (HttpContext context, string id, KeepsakeStore store) =>
        {
            if (!int.TryParse(context.Request.Headers["X-DV10-Consent"], out var consent)) { return Error(context, 400, "consent_required"); }
            if (context.Request.ContentLength is > 12 * 1024 * 1024) { return Error(context, 413, "request_too_large"); }
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            { limit.MaxRequestBodySize = 12 * 1024 * 1024; }
            var upload = await store.AcceptPhotoAsync(id, Actor(context), context.Request.Body,
                context.Request.ContentType ?? "", Key(context), consent, context.RequestAborted);
            return Results.Json(new { schemaVersion = 1, upload = new { upload.Id, upload.Width, upload.Height, upload.CreatedAt } }, statusCode: 201);
        });
        group.MapPost("/{id:guid}/portraits", async (HttpContext context, string id, KeepsakeStore store) =>
        {
            var request = await BodyAsync<PortraitRequest>(context);
            if (request.Variant == "portrait_with_wand") { return Error(context, 409, "portrait_variant_unsupported"); }
            if (request.Variant != "portrait_only") { return Error(context, 400, "portrait_variant_invalid"); }
            var actor = Actor(context);
            var view = store.Read(id, actor);
            if (!view.PortraitConsent || view.PortraitConsentVersion != request.ConsentVersion) { return Error(context, 409, "consent_required"); }
            var job = store.EnqueuePortrait(id, actor, request.UploadId, request.EditionId, request.Variant, true, Key(context));
            return Results.Json(new { schemaVersion = 1, job }, statusCode: 202);
        });
        foreach (var route in new[] { "portraits", "generation" })
        {
            var selectedRoute = route;
            group.MapPost("/{id:guid}/" + selectedRoute + "/{jobId:guid}/cancel", async (
                HttpContext context, string id, string jobId, KeepsakeStore store) =>
            {
                await BodyAsync<EmptyRequest>(context);
                store.Cancel(id, Actor(context), jobId, Key(context));
                return Results.NoContent();
            });
        }
        group.MapPost("/{id:guid}/exports", async (HttpContext context, string id, KeepsakeStore store) =>
        {
            var request = await BodyAsync<ExportRequest>(context);
            if (request.Format is not ("zip" or "card" or "story")) { return Error(context, 400, "invalid_export_format"); }
            var asset = store.CreateExport(id, Actor(context), request.EditionId, request.Format, request.PortraitId, Key(context));
            return Results.Json(new { schemaVersion = 1, asset }, statusCode: 201);
        });
        foreach (var scope in new[] { "", "/portrait" })
        {
            var selectedScope = scope;
            group.MapDelete("/{id:guid}" + selectedScope, async (HttpContext context, string id, KeepsakeStore store) =>
            {
                var request = await BodyAsync<DeleteRequest>(context);
                if (request.Confirm != "delete" || request.ExpectedRevision < 0) { return Error(context, 409, "confirmation_required"); }
                var receipt = store.Delete(id, Actor(context), selectedScope == "" ? KeepsakeDeleteScope.All : KeepsakeDeleteScope.PhotosAndPortraits,
                    Key(context), request.ExpectedRevision);
                return Results.Json(new { schemaVersion = 1, receipt = Receipt(receipt) }, statusCode: 202);
            });
        }
        group.MapGet("/deletions/{receiptId:guid}", (HttpContext context, string receiptId, KeepsakeStore store) =>
            Results.Json(new { schemaVersion = 1, receipt = Receipt(store.GetDeletionReceipt(receiptId, Actor(context))) }));
    }

    private static async Task<T> BodyAsync<T>(HttpContext context) where T : Request
    {
        if (!context.Request.HasJsonContentType()) { throw new KeepsakeException("json_required", 415); }
        if (context.Request.ContentLength is > 8192) { throw new KeepsakeException("request_too_large", 413); }
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        { limit.MaxRequestBodySize = 8192; }
        var request = await JsonSerializer.DeserializeAsync<T>(context.Request.Body, Json, context.RequestAborted);
        if (request is null || request.SchemaVersion != 1) { throw new KeepsakeException("invalid_request"); }
        return request;
    }

    private static string Key(HttpContext context) => context.Request.Headers["Idempotency-Key"].ToString();
    private static IResult Error(HttpContext context, int status, string code) =>
        Results.Json(new { type = "about:blank", title = code, status, code }, statusCode: status);
    private static IResult Link(KeepsakeLink link, string kind, IConfiguration config) => Results.Json(new
    { schemaVersion = 1, url = config["Keepsake:Origin"]!.TrimEnd('/') + "/keepsake/#" + kind + "=" + Uri.EscapeDataString(link.Token), link.ExpiresAt });
    private static object Receipt(KeepsakeDeletionReceipt receipt) => new
    { receipt.Id, receipt.Scope, accessRevoked = true, receipt.LocalState, receipt.ProviderState, receipt.BackupState, receipt.RequestedAt };
    private static string Extension(string media) => media switch
    { "image/png" => ".png", "image/jpeg" => ".jpg", "image/webp" => ".webp", "image/svg+xml" => ".svg", "application/zip" => ".zip", _ => ".txt" };

    private record Request(int SchemaVersion);
    private sealed record EmptyRequest(int SchemaVersion) : Request(SchemaVersion);
    private sealed record EditionRequest(int SchemaVersion, string EditionId) : Request(SchemaVersion);
    private sealed record AccountAssociationRequest(int SchemaVersion, string TargetAccountId) : Request(SchemaVersion);
    private sealed record BriefRequest(int SchemaVersion, KeepsakeBrief Brief) : Request(SchemaVersion);
    private sealed record DeleteRequest(int SchemaVersion, string Confirm, int ExpectedRevision) : Request(SchemaVersion);
    private sealed record ConsentRequest(int SchemaVersion, bool Accepted, string PolicyVersion) : Request(SchemaVersion);
    private sealed record PortraitRequest(int SchemaVersion, string UploadId, string EditionId, string Variant, int ConsentVersion) : Request(SchemaVersion);
    private sealed record ExportRequest(int SchemaVersion, string EditionId, string Format, string? PortraitId = null) : Request(SchemaVersion);
}
