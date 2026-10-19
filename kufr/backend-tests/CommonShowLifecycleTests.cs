using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Keepsakes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

internal static class CommonShowLifecycleTests
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "common-show-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { RunAsync(root).GetAwaiter().GetResult(); }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static async Task RunAsync(string root)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = Path.Combine(root, "journal.sqlite"),
            ["Keepsake:Enabled"] = "true", ["Keepsake:DataRoot"] = Path.Combine(root, "content"),
            ["Backend:Memory:Enabled"] = "false", ["Backend:Memory:LongTermPersonalEnabled"] = "false"
        }).Build();
        using var services = new ServiceCollection().BuildServiceProvider();
        using var journal = new OperationJournal(config, TimeProvider.System);
        var memory = new EveningMemoryStore(config, journal);
        var conversations = new ConversationStore(TimeProvider.System, journal, eveningMemory: memory);
        var request = new CreateConversationRequest("unused", Guid.NewGuid(), 0, PartyWandCatalog.Version,
            "cs-CZ", new(false, false));
        // All online transports enter through this authoritative common creation path.
        foreach (var device in new[] { "text-common", "realtime-common", "legacy-live-common" })
        {
            var key = Guid.NewGuid();
            var admitted = conversations.Create(device, key, "admit"u8, request);
            Check(admitted.Status == 201, "common session admitted");
            var id = admitted.Response!.ConversationId;
            var evening = memory.ReadCapturedShowEvening(device, id)!;
            Check(evening == memory.ReadActiveEveningId(device), "visit captures original active evening even with memory disabled");
            Check(memory.ReadChronicle(device, evening)!.CompletedVisits == 0, "dialogue admission alone is not physical completion");
            Check(conversations.Create(device, key, "admit"u8, request).Response!.ConversationId == id, "common admission retry stable");
            var cancellationKey = Guid.NewGuid();
            Check(conversations.EndConversation(device, id, cancellationKey).Status == 204, "session cancellation succeeds");
            Check(conversations.EndConversation(device, id, cancellationKey).Status == 204, "session cancellation replay is idempotent");
            Check(memory.ReadChronicle(device, evening)!.InterruptedVisits == 1, "cancel records one interrupted visit");
            Check(!memory.RecordConfirmedShowOutcome(device, id, evening, Guid.NewGuid().ToString("D"), 1, "completed_by_timing"),
                "interrupted session cannot become confirmed physical result");
        }

        const string physicalDevice = "physical-common";
        var created = conversations.Create(physicalDevice, Guid.NewGuid(), "physical"u8, request);
        var conversation = created.Response!.ConversationId;
        var original = memory.ReadCapturedShowEvening(physicalDevice, conversation)!;
        var proposal = new SelectionProposal(Guid.NewGuid().ToString("D"), conversation, Guid.NewGuid(),
            PartyWandCatalog.Version, 7, 2, "", request.BootId, 0, DateTimeOffset.UtcNow.AddMinutes(1));
        var correlation = new TurnCorrelation(physicalDevice, request.BootId, conversation, proposal.TurnId, 0, 7);
        var operation = journal.Reserve(physicalDevice, "/synthetic-selection", Guid.NewGuid(), "hash", correlation);
        Check(journal.MarkSelectionCompleted(operation.OperationId, proposal, original), "actual proposal journal path retains captured evening");
        Check(memory.ReadChronicle(physicalDevice, original)!.ConfirmedPresentations == 0,
            "completed dialogue proposal does not claim physical success");
        Check(conversations.EndConversation(physicalDevice, conversation, Guid.NewGuid()).Status == 204,
            "normal network cleanup after proposal succeeds");
        Check(memory.ReadChronicle(physicalDevice, original)!.InterruptedVisits == 0,
            "normal cleanup leaves pending physical result unresolved");
        memory.EndShowEvening(physicalDevice, original);
        var rotated = memory.Create(physicalDevice, Guid.NewGuid(), "rotate", "hash", original, "Další večer").Response!.ActiveEveningId;
        var access = new DeviceAccessStore(config, TimeProvider.System);
        var token = access.IssueToken(physicalDevice)!.AccessToken;
        var keepsakes = new KeepsakeStore(config, TimeProvider.System);
        var outcome = new KeepsakeOutcomeEndpoints.OutcomeRequest(1, Guid.NewGuid().ToString("D"), proposal.ProposalId,
            Guid.NewGuid().ToString("D"), proposal.BootId.ToString("D"), proposal.Generation, proposal.DeviceRevision, "completed_by_timing");
        async Task<int?> PublishAsync()
        {
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Headers.Authorization = "Bearer " + token;
            context.Request.Headers["Idempotency-Key"] = Guid.NewGuid().ToString("D");
            var body = JsonSerializer.SerializeToUtf8Bytes(outcome, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            context.Request.Body = new MemoryStream(body);
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = body.Length;
            var result = await KeepsakeOutcomeEndpoints.PublishAsync(context, operation.OperationId, access,
                journal, keepsakes, config, memory);
            return (result as IStatusCodeHttpResult)?.StatusCode;
        }
        Check(await PublishAsync() == 201 && await PublishAsync() == 201, "actual authenticated outcome endpoint is idempotent");
        var completed = memory.ReadChronicle(physicalDevice, original)!;
        Check(completed.CompletedVisits == 1 && completed.ConfirmedPresentations == 1 && completed.InterruptedVisits == 0,
            "actual physical endpoint completes one visit and one presentation in original closed evening");
        Check(memory.ReadChronicle(physicalDevice, rotated)!.ConfirmedPresentations == 0, "late completion cannot migrate to new evening");
        conversations.EndConversation(physicalDevice, conversation, Guid.NewGuid());
        Check(memory.ReadChronicle(physicalDevice, original)!.InterruptedVisits == 0, "cleanup after successful physical outcome cannot add interruption");

        await VerifyLegacyOutcomeAsync("legacy-pending", alreadyConfirmed: false, deleted: false);
        await VerifyLegacyOutcomeAsync("legacy-confirmed", alreadyConfirmed: true, deleted: false);
        await VerifyLegacyOutcomeAsync("legacy-deleted", alreadyConfirmed: true, deleted: true);

        async Task VerifyLegacyOutcomeAsync(string device, bool alreadyConfirmed, bool deleted)
        {
            var originalEvening = memory.GetStatus(device).EveningId;
            var legacyConversation = Guid.NewGuid().ToString("D");
            var legacyProposal = proposal with { ProposalId = Guid.NewGuid().ToString("D"), ConversationId = legacyConversation };
            var legacyCorrelation = correlation with { DeviceId = device, ConversationId = legacyConversation };
            var legacyOperation = journal.Reserve(device, "/legacy-selection", Guid.NewGuid(), "legacy-hash", legacyCorrelation);
            journal.MarkSelectionCompleted(legacyOperation.OperationId, legacyProposal, originalEvening);
            var legacyOutcome = outcome with { OutcomeId = Guid.NewGuid().ToString("D"), ProposalId = legacyProposal.ProposalId };
            if (alreadyConfirmed)
            {
                var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(legacyOutcome)));
                journal.ConfirmKeepsakeOutcome(device, legacyOperation.OperationId, legacyOutcome.OutcomeId, legacyOutcome.PresentationEvidence, hash);
            }
            Check(memory.ReadCapturedShowEvening(device, legacyConversation) is null, "preupgrade fixture has journal proof without new visit capture");
            if (deleted)
            {
                // Simulate a preupgrade deletion: archive tombstone exists,
                // while this store deliberately has no memory-revocation adapter.
                var wand = PartyWandCatalog.Available(63).Single(item => item.Compartment == 2);
                var old = keepsakes.Issue(new(legacyOutcome.OutcomeId, device, 2, wand.StableId, wand.DisplayName,
                    wand.Description, "completed_by_timing", PartyWandCatalog.Version, originalEvening));
                var oldOwner = keepsakes.Bootstrap();
                keepsakes.Claim(keepsakes.IssueClaim(old.Id, device).Token, oldOwner.Token, "legacy-claim");
                keepsakes.Delete(old.Id, oldOwner.Token, KeepsakeDeleteScope.All, "legacy-delete");
            }
            memory.EndShowEvening(device, originalEvening);
            var newEvening = memory.Create(device, Guid.NewGuid(), "rotate-legacy", "hash", originalEvening, "Další večer").Response!.ActiveEveningId;
            var legacyToken = access.IssueToken(device)!.AccessToken;
            async Task<int?> PublishLegacyAsync()
            {
                var context = new DefaultHttpContext { RequestServices = services };
                context.Request.Headers.Authorization = "Bearer " + legacyToken;
                context.Request.Headers["Idempotency-Key"] = Guid.NewGuid().ToString("D");
                var body = JsonSerializer.SerializeToUtf8Bytes(legacyOutcome, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                context.Request.Body = new MemoryStream(body);
                context.Request.ContentType = "application/json";
                context.Request.ContentLength = body.Length;
                var result = await KeepsakeOutcomeEndpoints.PublishAsync(context, legacyOperation.OperationId, access,
                    journal, keepsakes, config, memory);
                return (result as IStatusCodeHttpResult)?.StatusCode;
            }
            if (deleted)
            {
                Check(await PublishLegacyAsync() == 410, "preupgrade archive tombstone rejects physical retry before bootstrapping memory");
                Check(memory.ReadCapturedShowEvening(device, legacyConversation) is null
                    && memory.ReadChronicle(device, originalEvening)!.ConfirmedPresentations == 0,
                    "deleted preupgrade outcome cannot resurrect archived chronicle counts");
            }
            else
            {
                Check(await PublishLegacyAsync() == 201 && await PublishLegacyAsync() == 201,
                    "trusted pending/confirmed preupgrade journal outcomes survive upgrade and retries");
                Check(memory.ReadCapturedShowEvening(device, legacyConversation) == originalEvening,
                    "journal bootstrap captures exact original evening after closure and rotation");
                Check(memory.ReadChronicle(device, originalEvening) is { ConfirmedPresentations: 1, CompletedVisits: 1 }
                    && memory.ReadChronicle(device, newEvening)!.ConfirmedPresentations == 0,
                    "legacy completion remains unique in original historical evening");
                memory.RevokeShowSource(device, legacyOutcome.OutcomeId);
                Check(await PublishLegacyAsync() == 409, "memory tombstones also block trusted journal bootstrap replay");
            }
        }

        // Deterministic lock-order regression: admission owns the memory gate
        // while deletion reaches its revoke boundary. Its journal write must
        // still finish, because deletion holds no base DB write transaction.
        var deletionStore = new KeepsakeStore(config, TimeProvider.System, memoryStore: memory);
        var deletionEvening = memory.GetStatus("delete-race").EveningId;
        var deletion = deletionStore.Issue(new(Guid.NewGuid().ToString("D"), "delete-race", 1, "wand-1",
            "Syntetická hůlka", "Fixture", "completed_by_timing", EveningId: deletionEvening));
        var deletionOwner = deletionStore.Bootstrap();
        deletionStore.Claim(deletionStore.IssueClaim(deletion.Id, "delete-race").Token, deletionOwner.Token, "delete-race-claim");
        using var admissionInsideMemory = new ManualResetEventSlim();
        using var deleteAtRevocation = new ManualResetEventSlim();
        deletionStore.BeforeDeletionMemoryRevocation = () => deleteAtRevocation.Set();
        var concurrentAdmission = Task.Run(() => memory.TryAdmitShowVisit("admission-race", () =>
        {
            admissionInsideMemory.Set();
            if (!deleteAtRevocation.Wait(TimeSpan.FromSeconds(5))) { throw new TimeoutException("Deletion revoke boundary"); }
            return journal.ReserveCreate("admission-race", Guid.NewGuid(), "concurrent-hash").Response!.ConversationId;
        }));
        Check(admissionInsideMemory.Wait(TimeSpan.FromSeconds(5)), "admission holds memory gate for lock-order fixture");
        var concurrentDeletion = Task.Run(() => deletionStore.Delete(deletion.Id, deletionOwner.Token, KeepsakeDeleteScope.All, "concurrent-delete"));
        await Task.WhenAll(concurrentAdmission, concurrentDeletion).WaitAsync(TimeSpan.FromSeconds(10));
        Check(concurrentAdmission.Result is not null && concurrentDeletion.Result.LocalState == "local_cleanup_complete",
            "full deletion and common admission complete without memory/base-database lock inversion");

        const string closedDevice = "closed-common";
        var closedEvening = memory.GetStatus(closedDevice).EveningId;
        memory.EndShowEvening(closedDevice, closedEvening);
        var rejectedKey = Guid.NewGuid();
        Check(conversations.Create(closedDevice, rejectedKey, "closed"u8, request).Status == 409,
            "closed evening rejects common session before journal reservation");
        Check(journal.FindExistingCreate(closedDevice, rejectedKey, Convert.ToHexString(SHA256.HashData("closed"u8))) is null,
            "rejected admission leaves no journal phantom201 replay");
        var orphanKey = Guid.NewGuid();
        journal.ReserveCreate(closedDevice, orphanKey, Convert.ToHexString(SHA256.HashData("orphan"u8)));
        Check(conversations.Create(closedDevice, orphanKey, "orphan"u8, request).Status == 409,
            "crash gap journal reservation without capture cannot replay201 after closure");

        const string racingDevice = "race-common";
        var racingEvening = memory.GetStatus(racingDevice).EveningId;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var admission = Task.Run(() => memory.TryAdmitShowVisit(racingDevice, () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) { throw new TimeoutException("Synthetic admission release"); }
            return Guid.NewGuid().ToString("D");
        }));
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "admission reached serialized callback");
        var closure = Task.Run(() => memory.EndShowEvening(racingDevice, racingEvening));
        release.Set();
        Check(await admission == racingEvening && await closure is not null, "admission linearizes before concurrent close");
        var calledAfterClose = false;
        Check(memory.TryAdmitShowVisit(racingDevice, () => { calledAfterClose = true; return Guid.NewGuid().ToString("D"); }) is null
            && !calledAfterClose, "closure linearizes before later reserve callback");
        Console.WriteLine("Common online show lifecycle tests passed (synthetic admission/closure race, actual outcome endpoint, no paid calls).");
    }

    private static void Check(bool value, string message)
    {
        if (!value) { throw new InvalidOperationException("Common show lifecycle: " + message); }
    }
}
