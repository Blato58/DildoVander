using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Realtime;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

internal static class OperationEpochFlowTests
{
    private const string MaximumEpoch = "18446744073709551615";

    internal static Task RunAsync()
    {
        foreach (var gptLive in new[] { false, true })
        {
            foreach (var epoch in new string?[] { null, MaximumEpoch })
            {
                CapturedProposalKeepsRequestEpoch(gptLive, epoch);
            }
            AdmissionRejectsAnotherEpoch(gptLive);
        }
        AvailabilityCannotRestampAnActiveCall();
        ExplicitLegacyZeroMatchesAnAbsentEpoch();
        ImageHeadersPreserveTheOriginalEpoch();
        ImageScopeIsCheckedBeforeConsentAndMedia();
        ImageRetryHashKeepsLegacyZeroButSeparatesScopedEpochs();
        return Task.CompletedTask;
    }

    private static void CapturedProposalKeepsRequestEpoch(bool gptLive, string? epoch)
    {
        using var fixture = new Fixture(epoch);
        for (var index = 0; index < PersonalityCatalog.MinimumQuestions(PersonalityCatalog.DefaultId); index++)
        {
            var question = fixture.Admit(gptLive);
            Check(fixture.Store.CompleteRealtimeTurn(question, new("answer", "Another question?", "ask", "", null, 0)) is not null,
                "a deterministic answer advances the real question gate");
            fixture.Release(question, completed: true);
        }
        var call = fixture.Admit(gptLive);
        Check(call.Correlation.OperationEpoch == epoch && call.Context.Request.OperationEpoch == epoch,
            "both engines capture the original nullable request epoch");
        var result = gptLive
            ? fixture.Store.CompleteGptLive(call, Outcome(7))
            : fixture.Store.CompleteRealtimeTurn(call, new("answer", "Your wand is ready.", "propose", "fits", 1, 0));
        Check(result?.Proposal is SelectionProposal proposal && proposal.OperationEpoch == epoch,
            "the proposal retains the captured epoch");
        var operation = fixture.Store.GetOperation(Fixture.DeviceId, call.OperationId).Operation;
        Check(operation?.Correlation.OperationEpoch == epoch, "the published operation retains the captured correlation");
        var json = JsonSerializer.Serialize(result, RealtimeProtocol.JsonOptions);
        Check(epoch is null ? !json.Contains("operationEpoch", StringComparison.Ordinal)
            : json.Contains("\"operationEpoch\":\"" + epoch + "\"", StringComparison.Ordinal),
            "legacy proposals keep their old shape and scoped proposals use decimal strings");
        fixture.Release(call, completed: true);
    }

    private static void AdmissionRejectsAnotherEpoch(bool gptLive)
    {
        using var fixture = new Fixture(MaximumEpoch);
        foreach (var epoch in new string?[] { null, "0", "1" })
        {
            var request = fixture.Request(epoch);
            var rejected = fixture.Begin(request, gptLive);
            Check(rejected.Status == 409 && rejected.Code == "conversation_version_mismatch",
                "an immutable conversation rejects an absent, older or different scope");
        }
        foreach (var epoch in new[] { "", "00", "+1", "-1", " 1", "1 ", "18446744073709551616", "\u0661" })
        {
            Check(fixture.Begin(fixture.Request(epoch), gptLive).Status == 400,
                "noncanonical epoch input fails before admission");
        }
        var call = fixture.Admit(gptLive);
        Check(call.Correlation.OperationEpoch == MaximumEpoch, "rejections never replace the captured maximum epoch");
        Check(!fixture.Store.IsRealtimeTurnCurrent(call with { Correlation = call.Correlation with { OperationEpoch = "1" } }),
            "a changed callback correlation cannot borrow the active call");
        Check(!fixture.Store.IsRealtimeTurnCurrent(call with
            { Context = call.Context with { Request = call.Context.Request with { OperationEpoch = "1" } } }),
            "a changed callback request cannot be restamped from conversation state");
    }

    private static void AvailabilityCannotRestampAnActiveCall()
    {
        using var fixture = new Fixture(MaximumEpoch);
        var call = fixture.Admit(gptLive: true);
        var original = fixture.Store.LiveAvailability(call);
        Check(original == (63, 7, true), "Live starts with the captured availability");
        foreach (var epoch in new string?[] { null, "0", "1", "00", "18446744073709551616" })
        {
            Check(!fixture.Store.UpdateLiveAvailability(Fixture.DeviceId, fixture.ConversationId,
                fixture.BootId, 0, 31, 8, epoch), "availability cannot change operation scope");
            Check(fixture.Store.LiveAvailability(call) == original, "rejected scope leaves availability untouched");
        }
        Check(!fixture.Store.UpdateLiveAvailability("other-device", fixture.ConversationId,
            fixture.BootId, 0, 31, 8, MaximumEpoch), "availability preserves device authorization");
        Check(fixture.Store.UpdateLiveAvailability(Fixture.DeviceId, fixture.ConversationId,
            fixture.BootId, 0, 31, 8, MaximumEpoch), "a matching epoch may advance availability");
        Check(!fixture.Store.UpdateLiveAvailability(Fixture.DeviceId, fixture.ConversationId,
            fixture.BootId, 0, 63, 7, MaximumEpoch), "a stale availability revision remains rejected");
        Check(!fixture.Store.UpdateLiveAvailability(Fixture.DeviceId, fixture.ConversationId,
            fixture.BootId, 0, 63, 8, MaximumEpoch), "one revision cannot describe two masks");
        Check(fixture.Store.LiveAvailability(call) == (31, 8, true)
            && call.Correlation.OperationEpoch == MaximumEpoch && call.Correlation.DeviceRevision == 7,
            "availability updates never rewrite the active call's original snapshot");
        Check(fixture.Store.CompleteGptLive(call, Outcome(8)) is null,
            "a changed physical revision needs a new correlated interaction before a proposal");
    }

    private static void ExplicitLegacyZeroMatchesAnAbsentEpoch()
    {
        using var fixture = new Fixture(null);
        var call = fixture.Admit(gptLive: true, epoch: "0");
        Check(call.Correlation.OperationEpoch == "0", "an explicit legacy zero retains its original wire representation");
        Check(fixture.Store.UpdateLiveAvailability(Fixture.DeviceId, fixture.ConversationId,
            fixture.BootId, 0, 63, 7), "an absent epoch still matches an explicitly captured zero");
        Check(fixture.Store.LiveAvailability(call).Current, "legacy availability remains usable");
    }

    private static LiveOutcome Outcome(int availabilityRevision) => new(
        new("answer", "Your wand is ready.", "propose", "fits", 1, 0), 4, [])
    {
        Autonomous = true,
        PlaybackVerified = true,
        DecisionRevision = 2,
        Decision = new(WandDecisionStatus.ValidProposal, 1, [], "", 2, availabilityRevision)
    };

    private static void ImageHeadersPreserveTheOriginalEpoch()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Idempotency-Key"] = Guid.NewGuid().ToString("D");
        context.Request.Headers["Client-Turn-Id"] = Guid.NewGuid().ToString("D");
        context.Request.Headers["Boot-Id"] = Guid.NewGuid().ToString("D");
        context.Request.Headers["Generation"] = "0";
        context.Request.Headers["Device-Revision"] = "7";
        context.Request.Headers["Catalog-Version"] = PartyWandCatalog.Version;
        Check(CostumeImageEndpoints.ReadHeaders(context, out _, out var legacy) && legacy!.OperationEpoch is null,
            "an absent costume header retains the legacy nullable representation");
        context.Request.Headers["Operation-Epoch"] = MaximumEpoch;
        Check(CostumeImageEndpoints.ReadHeaders(context, out _, out var scoped) && scoped!.OperationEpoch == MaximumEpoch,
            "a costume upload captures the full unsigned maximum as a string");
        context.Request.Headers["Operation-Epoch"] = "0";
        Check(CostumeImageEndpoints.ReadHeaders(context, out _, out var zero) && zero!.OperationEpoch == "0",
            "an explicit canonical zero remains valid legacy input");
        foreach (var invalid in new[] { "", "00", "+1", "-1", " 1", "1 ", "18446744073709551616", "\u0661", "1,2" })
        {
            context.Request.Headers["Operation-Epoch"] = invalid;
            Check(!CostumeImageEndpoints.ReadHeaders(context, out _, out _), "present malformed costume epoch is rejected");
        }
        context.Request.Headers["Operation-Epoch"] = new Microsoft.Extensions.Primitives.StringValues(["1", "2"]);
        Check(!CostumeImageEndpoints.ReadHeaders(context, out _, out _), "duplicate costume epoch headers are rejected");
    }

    private static void ImageScopeIsCheckedBeforeConsentAndMedia()
    {
        using var fixture = new Fixture(MaximumEpoch);
        var upload = new CostumeImageUpload(Guid.NewGuid(), fixture.BootId, 0, 7,
            PartyWandCatalog.Version, CostumeImageTests.Jpeg(), "1")
        { ExplicitConsent = true, ImageId = Guid.NewGuid().ToString("D") };
        var key = Guid.NewGuid();
        Check(fixture.Store.SubmitImage(Fixture.DeviceId, fixture.ConversationId, key,
            upload with { OperationEpoch = "00" }).Status == 400, "direct noncanonical image epochs fail closed");
        var rejected = fixture.Store.SubmitImage(Fixture.DeviceId, fixture.ConversationId, key, upload);
        Check(rejected.Status == 409 && rejected.ErrorCode == "conversation_version_mismatch" && rejected.OperationId is null,
            "a wrong image scope is rejected before creating a journal operation");
        Check(fixture.Store.ReadVisualContext(Fixture.DeviceId, fixture.ConversationId).Revision == 0
            && fixture.ReadImage(upload.ImageId!) is null, "wrong scope archives no visual media or context");
        var matching = upload with { OperationEpoch = MaximumEpoch, ExplicitConsent = false };
        Check(fixture.Store.SubmitImage(Fixture.DeviceId, fixture.ConversationId, key, matching).Status == 403,
            "wrong scope cannot grant consent to the subsequent matching request");
        matching = matching with { ExplicitConsent = true };
        var accepted = fixture.Store.SubmitImage(Fixture.DeviceId, fixture.ConversationId, key, matching);
        Check(accepted.Status == 202 && accepted.OperationId is not null, "a matching explicitly consented image is accepted");
        Check(fixture.Store.GetOperation(Fixture.DeviceId, accepted.OperationId!).Operation?.Correlation.OperationEpoch == MaximumEpoch,
            "the deterministic image journal retains its original scoped correlation");
        Check(fixture.ReadImage(upload.ImageId!) is not null, "only the matching request archives its image");
        var corrected = fixture.Store.CorrectCostume(Fixture.DeviceId, fixture.ConversationId,
            Guid.NewGuid(), matching, "a blue cloak");
        Check(corrected.Status == 200 && corrected.Operation?.Correlation.OperationEpoch == MaximumEpoch,
            "manual visual correction retains the same captured epoch");
    }

    private static void ImageRetryHashKeepsLegacyZeroButSeparatesScopedEpochs()
    {
        using var fixture = new Fixture(null);
        var upload = new CostumeImageUpload(Guid.NewGuid(), fixture.BootId, 0, 7,
            PartyWandCatalog.Version, CostumeImageTests.Jpeg())
        { ExplicitConsent = true, ImageId = Guid.NewGuid().ToString("D") };
        var key = Guid.NewGuid();
        var accepted = fixture.Store.SubmitImage(Fixture.DeviceId, fixture.ConversationId, key, upload);
        Check(accepted.Status == 202 && accepted.OperationId is not null, "legacy image request is accepted");
        var zero = fixture.Store.SubmitImage(Fixture.DeviceId, fixture.ConversationId, key, upload with { OperationEpoch = "0" });
        Check(zero.Status == 202 && zero.OperationId == accepted.OperationId,
            "explicit zero retains the historical absent-epoch idempotency hash");
        var scoped = fixture.Store.SubmitImage(Fixture.DeviceId, fixture.ConversationId, key,
            upload with { OperationEpoch = MaximumEpoch });
        Check(scoped.Status == 409 && scoped.ErrorCode == "idempotency_conflict" && scoped.OperationId is null,
            "a scoped envelope cannot replay a legacy journal correlation before access validation");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new Exception("Operation epoch flow: " + message); }
    }

    private sealed class Fixture : IDisposable
    {
        internal const string DeviceId = "epoch-flow-device";
        private readonly string directory = Path.Combine(Path.GetTempPath(), "kufr-epoch-flow-" + Guid.NewGuid().ToString("N"));
        private readonly OperationJournal journal;
        private readonly string? epoch;
        private readonly List<RealtimeCall> active = [];
        private bool opening = true;

        internal Fixture(string? operationEpoch)
        {
            epoch = operationEpoch;
            Directory.CreateDirectory(directory);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:DatabasePath"] = Path.Combine(directory, "journal.sqlite")
            }).Build();
            journal = new OperationJournal(configuration, TimeProvider.System);
            Store = new ConversationStore(TimeProvider.System, journal, liveOptions: new() { Mode = LiveActorMode.AutonomousActor });
            var request = new CreateConversationRequest(DeviceId, BootId, 0, PartyWandCatalog.Version, "cs-CZ", new(true, false), epoch);
            var created = Store.Create(DeviceId, Guid.NewGuid(), JsonSerializer.SerializeToUtf8Bytes(request, RealtimeProtocol.JsonOptions), request);
            ConversationId = created.Response?.ConversationId ?? throw new Exception("Epoch fixture conversation admission failed.");
        }

        internal ConversationStore Store { get; }
        internal Guid BootId { get; } = Guid.NewGuid();
        internal string ConversationId { get; }
        internal VisualMediaRecord? ReadImage(string imageId) => journal.ReadVisualMedia(DeviceId, ConversationId, imageId);

        internal RealtimeTurnRequest Request(string? operationEpoch) => new(Guid.NewGuid(), BootId, 0, 63, 7,
            PartyWandCatalog.Version, opening, "", null, "marin", 1, operationEpoch);

        internal RealtimeAdmission Begin(RealtimeTurnRequest request, bool gptLive) => Store.BeginRealtimeTurn(
            DeviceId, ConversationId, Guid.NewGuid(), JsonSerializer.SerializeToUtf8Bytes(request, RealtimeProtocol.JsonOptions),
            request, true, default, default, handsFree: true, gptLive: gptLive);

        internal RealtimeCall Admit(bool gptLive, string? epoch = null)
        {
            var admission = Begin(Request(epoch ?? this.epoch), gptLive);
            var call = admission.Call ?? throw new Exception("Epoch fixture turn admission failed: " + admission.Code);
            active.Add(call);
            opening = false;
            Check(Store.StartRealtimeTurn(call), "the deterministic provider call starts after durable admission");
            return call;
        }

        internal void Release(RealtimeCall call, bool completed)
        {
            Store.ReleaseRealtimeTurn(call, dispatched: true, completed: completed);
            active.Remove(call);
        }

        public void Dispose()
        {
            foreach (var call in active) { Store.ReleaseRealtimeTurn(call, dispatched: true, completed: false); }
            journal.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
