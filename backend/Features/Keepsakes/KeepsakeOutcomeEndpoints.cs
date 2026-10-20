using Kufr.Backend.Features.Show;
using System.Security.Cryptography;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Memory;

namespace Kufr.Backend.Features.Keepsakes;

internal static partial class KeepsakeOutcomeEndpoints
{
    internal sealed record OutcomeRequest(int SchemaVersion, string OutcomeId, string ProposalId,
        string PhysicalOperationId, string BootId, int Generation, int DeviceRevision,
        string PresentationEvidence,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string? OperationEpoch = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string? PhysicalFingerprint = null);

    internal sealed record OutcomeAcknowledgement(int SchemaVersion, string Locator, string OutcomeId,
        int SessionEpoch, string PhysicalOperationId,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string? OperationEpoch = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string? PhysicalFingerprint = null);

    internal static bool HasValidIdentityFields(System.Text.Json.JsonElement body)
    {
        if (body.ValueKind != System.Text.Json.JsonValueKind.Object) { return false; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in body.EnumerateObject())
        {
            var epoch = string.Equals(property.Name, "operationEpoch", StringComparison.OrdinalIgnoreCase);
            var fingerprint = string.Equals(property.Name, "physicalFingerprint", StringComparison.OrdinalIgnoreCase);
            if (!epoch && !fingerprint) { continue; }
            var canonicalName = epoch ? "operationEpoch" : "physicalFingerprint";
            if (property.Name != canonicalName || !seen.Add(canonicalName)
                || property.Value.ValueKind != System.Text.Json.JsonValueKind.String
                || !Conversations.OperationEpoch.IsValid(property.Value.GetString()))
            {
                return false;
            }
        }
        return true;
    }

    internal static async Task<IResult> PublishAsync(HttpContext c, string operationId,
        DeviceAccessStore access, OperationJournal journal, KeepsakeStore store, IConfiguration config,
        EveningMemoryStore memoryStore, HybridMemoryService memory)
    {
        if (!config.GetValue<bool>("Keepsake:Enabled")) { return Results.NotFound(); }
        var device = DeviceAccessEndpoints.Authenticate(c, access);
        if (device is null) { return Results.Unauthorized(); }
        if (!Guid.TryParseExact(c.Request.Headers["Idempotency-Key"], "D", out _)) { return Results.BadRequest(); }
        try
        {
            var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
                { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
                  RespectRequiredConstructorParameters = true };
            var body = await c.Request.ReadFromJsonAsync<System.Text.Json.JsonElement>(options, c.RequestAborted);
            if (!HasValidIdentityFields(body)) { return Results.BadRequest(); }
            var request = body.Deserialize<OutcomeRequest>(options);
            var proposal = journal.ReadKeepsakeSelection(device, operationId);
            if (request is null || request.SchemaVersion != 1 || proposal is null
                || !Guid.TryParseExact(request.OutcomeId, "D", out _)
                || !Guid.TryParseExact(request.PhysicalOperationId, "D", out _)
                || request.ProposalId != proposal.ProposalId || request.BootId != proposal.BootId.ToString("D")
                || request.Generation != proposal.Generation || request.DeviceRevision != proposal.DeviceRevision
                || !Conversations.OperationEpoch.Same(request.OperationEpoch, proposal.OperationEpoch)
                || !Conversations.OperationEpoch.IsValid(request.PhysicalFingerprint)
                || Conversations.OperationEpoch.Key(request.OperationEpoch) != "0"
                    && (request.PhysicalFingerprint is null or "0"
                        || !string.Equals(request.PhysicalOperationId, proposal.ProposalId, StringComparison.OrdinalIgnoreCase))
                || proposal.CatalogVersion != PartyWandCatalog.Version
                || request.PresentationEvidence != "completed_by_timing")
            { return Results.Json(new { errorCode = "outcome_not_eligible" }, statusCode: 409); }
            // The trusted device reports its calibrated sequence, not a position
            // sensor or delivery to a guest's hand. No second physical action.
            var bodyHash = Convert.ToHexString(SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request)));
            var outcome = journal.ConfirmKeepsakeOutcome(device, operationId, request.OutcomeId,
                request.PresentationEvidence, bodyHash);
            store.RequireOutcomeNotDeleted(device, outcome);
            var originalCapture = memoryStore.ReadCapturedShowEvening(device, proposal.ConversationId);
            var journalEvening = journal.ReadKeepsakeEvening(device, operationId);
            var capturedEvening = originalCapture ?? journalEvening;
            var recorded = originalCapture is not null
                ? memoryStore.RecordConfirmedShowOutcome(device, proposal.ConversationId, originalCapture,
                    outcome, proposal.Compartment, request.PresentationEvidence)
                : journalEvening is null || memoryStore.RecordJournalConfirmedShowOutcome(journal, device,
                    operationId, outcome, request.PresentationEvidence);
            if (!recorded)
            {
                return Results.Json(new { errorCode = "outcome_not_eligible" }, statusCode: 409);
            }
            c.RequestServices.GetService<ShowCoordinator>()?.ObserveFinaleOutcome(device, operationId,
                proposal.ProposalId, outcome, request.PresentationEvidence);
            var wand = PartyWandCatalog.Available(63).Single(w => w.Compartment == proposal.Compartment);
            var result = store.Issue(new KeepsakeIssueSnapshot(outcome, device, proposal.Compartment,
                wand.StableId, wand.DisplayName, wand.Description, request.PresentationEvidence,
                proposal.CatalogVersion, capturedEvening, proposal.ConversationId));
            if (capturedEvening is { } evening
                && journal.ReadSelectionMemoryEpoch(device, operationId) is { } deletionEpoch)
            {
                var permission = memoryStore.GuestPermission(device, proposal.ConversationId);
                memory.RecordConfirmedOutcome(new(device, evening, proposal.ConversationId,
                    journal.ReadSelectionPersonality(device, operationId), permission), operationId, outcome, wand.DisplayName,
                    request.PresentationEvidence, deletionEpoch);
            }
            return Results.Json(new OutcomeAcknowledgement(1, result.Id, outcome, proposal.Generation,
                request.PhysicalOperationId, request.OperationEpoch, request.PhysicalFingerprint), statusCode: 201);
        }
        catch (KeepsakeException ex) { return Results.Json(new { errorCode = ex.Code }, statusCode: ex.Status); }
        catch (System.Text.Json.JsonException) { return Results.BadRequest(); }
    }

    internal static IResult IssueClaim(HttpContext c, string id, DeviceAccessStore access,
        KeepsakeStore store, IConfiguration config)
    {
        if (!config.GetValue<bool>("Keepsake:Enabled")) { return Results.NotFound(); }
        var device = DeviceAccessEndpoints.Authenticate(c, access);
        if (device is null) { return Results.Unauthorized(); }
        if (!Guid.TryParseExact(c.Request.Headers["Idempotency-Key"], "D", out _)) { return Results.BadRequest(); }
        try
        {
            var link = store.IssueClaim(id, device, c.Request.Headers["Idempotency-Key"].ToString());
            return Results.Json(new { schemaVersion = 1, locator = id, url = config["Keepsake:Origin"]!.TrimEnd('/')
                + "/keepsake/#c=" + Uri.EscapeDataString(link.Token), link.ExpiresAt });
        }
        catch (KeepsakeException ex) { return Results.Json(new { errorCode = ex.Code }, statusCode: ex.Status); }
    }
}
