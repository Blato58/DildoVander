using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Microsoft.AspNetCore.Http.Features;

namespace Kufr.Backend.Features.Keepsakes;

internal static partial class KeepsakeEndpoints
{
    internal const string CookieName = "__Host-dv10";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    public static IEndpointRouteBuilder MapKeepsakes(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/guest/v1").RequireRateLimiting("keepsake");
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var config = http.RequestServices.GetRequiredService<IConfiguration>();
            if (!config.GetValue<bool>("Keepsake:Enabled")) { return Error(404, "access_unavailable"); }
            if (!CanonicalRequest(http, config)) { return Error(403, "origin_required"); }
            if (http.Request.Method != "GET" && !http.Request.Path.Equals("/guest/v1/bootstrap"))
            {
                if (ShareOnly(http)) { return Error(404, "access_unavailable"); }
                var store = http.RequestServices.GetRequiredService<KeepsakeStore>();
                if (!Guid.TryParseExact(http.Request.Headers["Idempotency-Key"], "D", out _))
                { return Error(400, "invalid_idempotency_key"); }
                if (!store.ValidateSession(Session(http), http.Request.Headers["X-DV10-CSRF"].ToString()))
                { return Error(403, "csrf_required"); }
            }
            try { return await next(context); }
            catch (KeepsakeException ex)
            {
                return ex.Code is "invalid_session" or "invalid_link" or "claim_already_used" or "keepsake_not_found" or "asset_not_found" or "deletion_receipt_not_found"
                    ? Error(404, "access_unavailable") : Error(ex.Status, ex.Code);
            }
            catch (JsonException) { return Error(400, "invalid_request"); }
            catch (BadHttpRequestException ex) when (ex.StatusCode == 413) { return Error(413, "request_too_large"); }
        });

        MapAccountAssociations(group);
        group.MapPost("/bootstrap", async (HttpContext context, KeepsakeStore store) =>
        {
            await BodyAsync<EmptyRequest>(context);
            var session = store.ValidateSession(Session(context)) ? store.ResumeSession(Session(context))! : store.Bootstrap();
            // Reuse a valid browser identity, so claiming another keepsake cannot
            // silently replace existing tabs' grants. The CSRF token is stored.
            context.Response.Cookies.Append(CookieName, session.Token,
                new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax,
                    Path = "/", MaxAge = TimeSpan.FromDays(30), IsEssential = true });
            return Results.Json(new { schemaVersion = 1, csrfToken = session.CsrfToken });
        });
        foreach (var kind in new[] { "claim", "recover", "share-claim" })
        {
            var exchange = kind;
            group.MapPost("/" + exchange, async (HttpContext context, KeepsakeStore store) =>
            {
                var request = await BodyAsync<TokenRequest>(context);
                if (request.Token is not { Length: >= 32 and <= 256 }) { return Error(404, "access_unavailable"); }
                var locator = exchange switch
                {
                    "claim" => store.Claim(request.Token, Session(context), Key(context)),
                    "recover" => store.Recover(request.Token, Session(context), Key(context)),
                    _ => store.ShareClaim(request.Token, Session(context), Key(context))
                };
                return Results.Json(new { schemaVersion = 1, locator,
                    editionId = exchange == "share-claim" ? store.ResolveShareEdition(request.Token, Session(context)) : null });
            });
        }
        group.MapGet("/keepsakes/{id:guid}", (HttpContext c, string id, KeepsakeStore store) =>
            Results.Json(Project(store.Read(id, Session(c), ShareOnly(c), c.Request.Query["editionId"].ToString()))));
        group.MapGet("/keepsakes/{id:guid}/history", (HttpContext c, string id, KeepsakeStore store) =>
        {
            if (ShareOnly(c) || !store.Authorize(id, Session(c), KeepsakeScope.Write))
            { return Error(404, "access_unavailable"); }
            return Results.Json(Project(store.Read(id, Session(c))));
        });
        group.MapGet("/keepsakes/{id:guid}/assets/{assetId:guid}", (HttpContext c, string id, string assetId, KeepsakeStore store) =>
        {
            var content = store.ReadAsset(id, Session(c), assetId, ShareOnly(c), c.Request.Query["editionId"].ToString());
            c.Response.Headers.ContentDisposition = "attachment; filename=\"dildovander-" + content.Asset.Kind + Extension(content.Asset.MediaType) + "\"";
            c.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(content.Bytes, content.Asset.MediaType, enableRangeProcessing: false);
        });
        group.MapPost("/keepsakes/{id:guid}/recovery-links", async (HttpContext c, string id, KeepsakeStore store, IConfiguration config) =>
        {
            await BodyAsync<EmptyRequest>(c);
            return Link(store.CreateRecovery(id, Session(c), Key(c)), "r", config);
        });
        group.MapPost("/keepsakes/{id:guid}/share-links", async (HttpContext c, string id, KeepsakeStore store, IConfiguration config) =>
        {
            var request = await BodyAsync<EditionRequest>(c);
            var edition = store.Read(id, Session(c)).Editions.SingleOrDefault(e => e.Id == request.EditionId);
            if (edition is null || string.IsNullOrEmpty(edition.ArtworkAssetId)) { return Error(409, "edition_not_ready"); }
            // A share is read-only. Prepare its base-only downloads while the
            // owner explicitly creates the link, never on the viewer's GET.
            foreach (var format in new[] { "story", "card", "zip" })
            {
                store.CreateExport(id, Session(c), request.EditionId, format, null, Key(c) + "/share-" + format);
            }
            return Link(store.CreateShare(id, Session(c), request.EditionId, Key(c)), "s", config);
        });
        group.MapDelete("/keepsakes/{id:guid}/recovery-links", async (HttpContext c, string id, KeepsakeStore store) =>
        {
            await BodyAsync<EmptyRequest>(c); store.RevokeRecovery(id, Session(c)); return Results.NoContent();
        });
        group.MapDelete("/keepsakes/{id:guid}/share-links", async (HttpContext c, string id, KeepsakeStore store) =>
        {
            await BodyAsync<EmptyRequest>(c); store.RevokeShares(id, Session(c)); return Results.NoContent();
        });
        group.MapPost("/keepsakes/{id:guid}/revoke-access", async (HttpContext c, string id, KeepsakeStore store) =>
        {
            var request = await BodyAsync<DeleteRequest>(c);
            VerifyConfirmation(request, store.Read(id, Session(c)).Revision);
            store.RevokeAllAccess(id, Session(c)); return Results.NoContent();
        });
        group.MapPost("/keepsakes/{id:guid}/portrait-consents", async (HttpContext c, string id, KeepsakeStore store) =>
        {
            var request = await BodyAsync<ConsentRequest>(c);
            var version = store.SetPortraitConsent(id, Session(c), request.Accepted, request.PolicyVersion, Key(c));
            return Results.Json(new { schemaVersion = 1, consentVersion = version });
        });
        group.MapPost("/keepsakes/{id:guid}/portrait-uploads", async (HttpContext c, string id, KeepsakeStore store) =>
        {
            if (!int.TryParse(c.Request.Headers["X-DV10-Consent"], out var consent)) { return Error(400, "consent_required"); }
            if (c.Request.ContentLength is > 12 * 1024 * 1024) { return Error(413, "request_too_large"); }
            var limit = c.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) { limit.MaxRequestBodySize = 12 * 1024 * 1024; }
            var upload = await store.AcceptPhotoAsync(id, Session(c), c.Request.Body,
                c.Request.ContentType ?? "", Key(c), consent, c.RequestAborted);
            return Results.Json(new { schemaVersion = 1, upload = new { upload.Id, upload.Width, upload.Height, upload.CreatedAt } }, statusCode: 201);
        });
        group.MapPost("/keepsakes/{id:guid}/portraits", async (HttpContext c, string id, KeepsakeStore store) =>
        {
            var request = await BodyAsync<PortraitRequest>(c);
            if (request.Variant == "portrait_with_wand") { return Error(409, "portrait_variant_unsupported"); }
            if (request.Variant != "portrait_only") { return Error(400, "portrait_variant_invalid"); }
            var view = store.Read(id, Session(c));
            if (!view.PortraitConsent || view.PortraitConsentVersion != request.ConsentVersion) { return Error(409, "consent_required"); }
            var job = store.EnqueuePortrait(id, Session(c), request.UploadId, request.EditionId,
                request.Variant, true, Key(c));
            return Results.Json(new { schemaVersion = 1, job }, statusCode: 202);
        });
        group.MapPost("/keepsakes/{id:guid}/portraits/{jobId:guid}/cancel", async (HttpContext c, string id, string jobId, KeepsakeStore store) =>
        {
            await BodyAsync<EmptyRequest>(c); store.Cancel(id, Session(c), jobId, Key(c)); return Results.NoContent();
        });
        group.MapPost("/keepsakes/{id:guid}/generation/{jobId:guid}/cancel", async (HttpContext c, string id, string jobId, KeepsakeStore store) =>
        {
            await BodyAsync<EmptyRequest>(c); store.Cancel(id, Session(c), jobId, Key(c)); return Results.NoContent();
        });
        group.MapPost("/keepsakes/{id:guid}/exports", async (HttpContext c, string id, KeepsakeStore store) =>
        {
            var request = await BodyAsync<ExportRequest>(c);
            if (request.Format is not ("zip" or "card" or "story")) { return Error(400, "invalid_export_format"); }
            var asset = store.CreateExport(id, Session(c), request.EditionId, request.Format, request.PortraitId, Key(c));
            return Results.Json(new { schemaVersion = 1, asset }, statusCode: 201);
        });
        foreach (var scope in new[] { "", "/portrait" })
        {
            var selected = scope;
            group.MapDelete("/keepsakes/{id:guid}" + selected, async (HttpContext c, string id, KeepsakeStore store) =>
            {
                var request = await BodyAsync<DeleteRequest>(c);
                if (request.Confirm != "delete" || request.ExpectedRevision < 0) { return Error(409, "confirmation_required"); }
                var receipt = store.Delete(id, Session(c), selected == "" ? KeepsakeDeleteScope.All : KeepsakeDeleteScope.PhotosAndPortraits, Key(c), request.ExpectedRevision);
                return Results.Json(new { schemaVersion = 1, receipt = Receipt(receipt) }, statusCode: 202);
            });
        }
        group.MapGet("/deletions/{receiptId:guid}", (HttpContext c, string receiptId, KeepsakeStore store) =>
            Results.Json(new { schemaVersion = 1, receipt = Receipt(store.GetDeletionReceipt(receiptId, Session(c))) }));

        app.MapPost("/v1/keepsakes/outcomes/{operationId:guid}", KeepsakeOutcomeEndpoints.PublishAsync);
        app.MapPost("/v1/keepsakes/{id:guid}/claim", KeepsakeOutcomeEndpoints.IssueClaim);
        app.MapPost("/v1/keepsakes/{id:guid}/restore-owner", async (HttpContext c, string id, ServerAdminAccess admin, KeepsakeStore store, IConfiguration config) =>
        {
            if (!admin.Authenticate(c)) { return Results.Unauthorized(); }
            if (!Guid.TryParseExact(c.Request.Headers["Idempotency-Key"], "D", out _)) { return Error(400, "invalid_idempotency_key"); }
            try
            {
                var request = await BodyAsync<RestoreOwnerRequest>(c);
                if (request.Confirm != "revoke_prior_access") { return Error(409, "confirmation_required"); }
                return Link(store.RestoreOwnerClaim(id, Key(c)), "c", config);
            }
            catch (KeepsakeException ex) { return Error(ex.Status, ex.Code); }
            catch (JsonException) { return Error(400, "invalid_request"); }
        });
        return app;
    }

    private static object Receipt(KeepsakeDeletionReceipt receipt) => new
    { receipt.Id, receipt.Scope, accessRevoked = true, receipt.LocalState, receipt.ProviderState, receipt.BackupState, receipt.RequestedAt };

    private static object Project(KeepsakeView v) => new
    {
        schemaVersion = 1, locator = v.Id, v.Revision, v.RetentionMode, deleteAfterUtc = (string?)null,
        v.State, v.ReadOnly, v.PortraitConsent, v.PortraitConsentVersion,
        snapshot = new { v.Snapshot.OfficialName, v.Snapshot.CatalogId, v.Snapshot.CatalogVersion, v.Snapshot.Slot, v.Snapshot.PhysicalEvidence },
        editions = v.Editions.Select(e => new { e.Id, e.Story, e.ArtworkAssetId, e.Synthetic, e.PublicationState, e.CreatedAt }),
        portraits = v.Portraits.Select(p => new { p.Id, p.EditionId, p.UploadId, p.Variant, p.AssetId, p.Synthetic, p.CreatedAt }),
        uploads = v.Uploads.Select(u => new { u.Id, u.Width, u.Height, u.CreatedAt }),
        jobs = v.Jobs.Select(j => new { j.Id, j.Kind, j.State, j.EditionId, j.UploadId, j.ErrorCode }),
        assets = v.Assets.Select(a => new { a.Id, a.Kind, a.MediaType, a.Length, a.EditionId, a.CreatedAt }), v.SharedEditionId
    };

    private static IResult Link(KeepsakeLink link, string kind, IConfiguration config) => Results.Json(new
    { schemaVersion = 1, url = config["Keepsake:Origin"]!.TrimEnd('/') + "/keepsake/#" + kind + "=" + Uri.EscapeDataString(link.Token), link.ExpiresAt });
    private static IResult Error(int status, string code) => Results.Json(new { schemaVersion = 1, errorCode = code, code, status }, statusCode: status);
    private static string Session(HttpContext c) => c.Request.Cookies[CookieName] ?? "";
    private static string Key(HttpContext c) => c.Request.Headers["Idempotency-Key"].ToString();
    private static bool ShareOnly(HttpContext c) => c.Request.Query["view"] == "share";
    private static string Extension(string media) => media switch
    { "image/png" => ".png", "image/jpeg" => ".jpg", "image/webp" => ".webp", "image/svg+xml" => ".svg", "application/zip" => ".zip", _ => ".txt" };
    private static void VerifyConfirmation(DeleteRequest request, int revision)
    {
        if (request.Confirm != "delete" || request.ExpectedRevision != revision) { throw new KeepsakeException("confirmation_required", 409); }
    }
    private static bool CanonicalRequest(HttpContext c, IConfiguration config)
    {
        if (!Uri.TryCreate(config["Keepsake:Origin"], UriKind.Absolute, out var origin)
            || origin.Scheme != "https" || origin.PathAndQuery != "/" || !string.IsNullOrEmpty(origin.Fragment)
            || !string.IsNullOrEmpty(origin.UserInfo)) { return false; }
        if (!c.Request.IsHttps || !string.Equals(c.Request.Host.Value, origin.Authority, StringComparison.OrdinalIgnoreCase)) { return false; }
        return c.Request.Method == "GET" || c.Request.Headers.Origin.ToString().TrimEnd('/') == origin.GetLeftPart(UriPartial.Authority);
    }
    private static async Task<T> BodyAsync<T>(HttpContext c) where T : VersionRequest
    {
        if (!c.Request.HasJsonContentType()) { throw new KeepsakeException("json_required", 415); }
        var limit = c.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) { limit.MaxRequestBodySize = 8192; }
        if (c.Request.ContentLength is > 8192) { throw new KeepsakeException("request_too_large", 413); }
        var request = await JsonSerializer.DeserializeAsync<T>(c.Request.Body, Json, c.RequestAborted);
        if (request is null || request.SchemaVersion != 1) { throw new KeepsakeException("invalid_request"); }
        return request;
    }
    private record VersionRequest(int SchemaVersion);
    private sealed record EmptyRequest(int SchemaVersion) : VersionRequest(SchemaVersion);
    private sealed record TokenRequest(int SchemaVersion, string Token) : VersionRequest(SchemaVersion);
    private sealed record EditionRequest(int SchemaVersion, string EditionId) : VersionRequest(SchemaVersion);
    private sealed record DeleteRequest(int SchemaVersion, string Confirm, int ExpectedRevision) : VersionRequest(SchemaVersion);
    private sealed record ConsentRequest(int SchemaVersion, bool Accepted, string PolicyVersion) : VersionRequest(SchemaVersion);
    private sealed record PortraitRequest(int SchemaVersion, string UploadId, string EditionId, string Variant, int ConsentVersion) : VersionRequest(SchemaVersion);
    private sealed record ExportRequest(int SchemaVersion, string EditionId, string Format, string? PortraitId = null) : VersionRequest(SchemaVersion);
    private sealed record RestoreOwnerRequest(int SchemaVersion, string Confirm) : VersionRequest(SchemaVersion);
}
