using System.Net;
using System.Text;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Memory;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

internal static class DependableMemoryTests
{
    internal static async Task RunAsync()
    {
        await VerifyPermittedContinuityAsync();
        await VerifyQueuedMemoryMutationFencesAsync();
        using var fixture = new Fixture();
        var store = fixture.Store;
        var legacyConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:Memory:Enabled"] = "false",
            ["Backend:Memory:LongTermPersonalEnabled"] = "false"
        }).Build();
        var migratedOptions = HybridMemoryOptions.Read(legacyConfiguration);
        Check(migratedOptions.Enabled && migratedOptions.LongTermPersonalEnabled && !migratedOptions.CloudIndexingEnabled,
            "legacy disabled flags cannot disable canonical local memory or explicit returning-guest continuity");
        var original = fixture.Access;
        var guestId = Guid.NewGuid().ToString("D");
        var permission = store.SetGuestPermission(original.DeviceId, original.ConversationId, guestId,
            "operator-binding", true, true, true, 0)!;
        var access = original with { Guest = permission };
        var fact = store.WriteMemory(access, new(Guid.NewGuid().ToString("D"), MemoryScope.Personal,
            MemoryKind.UserReportedFact, "Moje oblíbené hodiny jsou fialové.", "input:clock"))!;
        var unchangedEpoch = store.MemoryDeletionEpoch;
        var repeated = store.SetGuestPermission(access.DeviceId, access.ConversationId, guestId,
            "operator-binding", true, true, true, permission.Version)!;
        Check(repeated.Version == permission.Version && store.MemoryDeletionEpoch == unchangedEpoch,
            "unchanged permissions do not erase facts or invalidate their version");

        for (var index = 0; index < 240; index++)
        {
            var unrelated = original with { ConversationId = Guid.NewGuid().ToString("D") };
            Check(store.WriteMemory(unrelated, new(Guid.NewGuid().ToString("D"), MemoryScope.Conversation,
                MemoryKind.UserReportedFact, "Nesouvisející novější host " + index, "input:" + index)) is not null,
                "unrelated archive record committed");
        }
        var returning = original with { ConversationId = Guid.NewGuid().ToString("D") };
        returning = returning with { Guest = store.SetGuestPermission(returning.DeviceId, returning.ConversationId,
            guestId, "operator-return", true, true, true, 0) };
        Check(store.LocalMemory(returning, MemoryScope.Personal).Single().MemoryId == fact.MemoryId,
            "scope and verified identity eligibility precede retrieval limits");
        using var handler = new SearchFixture();
        using var provider = new OpenAiVectorMemoryProvider(new HttpClient(handler), "fixture", fixture.Options);
        var service = new HybridMemoryService(store, provider, fixture.Options, fixture.Clock);
        var zero = await service.RetrieveAsync(returning, MemoryScope.Personal, "hodiny", CancellationToken.None);
        Check(zero.Records.Single().MemoryId == fact.MemoryId,
            "successful empty cloud search cannot hide a newly committed local fact");
        handler.Fail = true;
        service.ClearCache();
        var outage = await service.RetrieveAsync(returning, MemoryScope.Personal, "hodiny", CancellationToken.None);
        Check(outage.Records.Single().MemoryId == fact.MemoryId && service.Status.Degraded,
            "provider outage leaves canonical personal recall usable and diagnostic");
        handler.Fail = false;
        using var offline = new OpenAiVectorMemoryProvider(new HttpClient(new SearchFixture()), null, fixture.Options);
        var offlineService = new HybridMemoryService(store, offline, fixture.Options, fixture.Clock);
        Check((await offlineService.RetrieveAsync(returning, MemoryScope.Personal, "hodiny", CancellationToken.None))
            .Records.Single().MemoryId == fact.MemoryId, "cloud disabled or unconfigured retains local continuity");
        var returningStartup = await offlineService.StartupContextAsync(returning, CancellationToken.None);
        var anonymousStartup = await offlineService.StartupContextAsync(returning with { Guest = null }, CancellationToken.None);
        Check(returningStartup.Text.Contains("fialové", StringComparison.Ordinal)
            && !anonymousStartup.Text.Contains("fialové", StringComparison.Ordinal),
            "both engines receive permitted returning history at startup while unbound guests do not");
        handler.Timeout = true;
        service.ClearCache();
        Check((await service.RetrieveAsync(returning, MemoryScope.Personal, "hodiny", CancellationToken.None))
            .Records.Single().MemoryId == fact.MemoryId, "provider timeout cannot conceal local facts");
        handler.Timeout = false;

        store.SetGuestPermission(access.DeviceId, access.ConversationId, Guid.NewGuid().ToString("D"),
            "different-operator-binding", true, true, false, permission.Version);
        Check(store.CurrentMemory(fact.MemoryId) is { IsActive: true, Text.Length: > 0 }
            && store.LocalMemory(returning, MemoryScope.Personal).Single().MemoryId == fact.MemoryId,
            "rebinding an origin changes access without deleting the previous guest archive");
        Check(store.LocalMemory(access, MemoryScope.Personal).Count == 0, "stale guest binding cannot read personal memory");

        var current = original with { ConversationId = Guid.NewGuid().ToString("D") };
        var generation = service.StoreEpochFor(current);
        await service.PersistContextAsync(current, "generation-a", [
            new("event-one", "user", "První dokončená odpověď.", 1, 2, "provider-one"),
            new("event-two", "user", "Druhá dokončená odpověď.", 3, 4, "provider-two")], [
            new("input:event-one", "První dokončená odpověď."),
            new("input:event-two", "Druhá dokončená odpověď.")], generation, CancellationToken.None);
        Check(store.LocalMemory(current, MemoryScope.Conversation).Count == 2,
            "a finalized batch persists all statements across ordinary write epochs");
        Check(store.Append(current.DeviceId, current.EveningId, "private-exchange", current.ConversationId,
            "Soukromé hodiny jsou fialové.", "Rozumím.", expectedDeletionEpoch: store.MemoryDeletionEpochFor(current.DeviceId, current.ConversationId)), "archive exchange committed");
        Check(!store.ReadContext(current.DeviceId, "unbound-next-guest", "hodiny").Transcript.Contains("fialové"),
            "anonymous evening context never injects another guest raw conversation");

        var correction = store.LocalMemory(current, MemoryScope.Conversation).First();
        var deletionBeforeCorrection = store.MemoryDeletionEpochFor(current.DeviceId, current.ConversationId);
        var factsBeforeCorrection = service.StoreEpochFor(current);
        var unrelatedDeletion = store.MemoryDeletionEpochFor("another-device", "another-conversation");
        var updated = store.WriteMemory(current, new(correction.MemoryId, correction.Scope, correction.Kind,
            "Opravená skutečnost.", "input:correction", correction.Revision))!;
        Check(store.MemoryDeletionEpochFor(current.DeviceId, current.ConversationId) == deletionBeforeCorrection
            && service.StoreEpochFor(current) != factsBeforeCorrection,
            "ordinary correction supersedes pending facts without killing the active provider session");
        await service.PersistContextAsync(current, "obsolete-fact-generation", [],
            [new("old-summary", "Zastaralé shrnutí před opravou.")], factsBeforeCorrection, CancellationToken.None);
        Check(store.LocalMemory(current).All(record => !record.Text.Contains("Zastaralé", StringComparison.Ordinal)),
            "late pre-correction derivation cannot restore superseded facts");
        Check(ReadScalar(fixture, "SELECT text FROM memory_records WHERE memory_id='" + correction.MemoryId + "' AND revision=1")
            == correction.Text, "corrections preserve historical revision text in the archive");
        fixture.Clock.Advance(TimeSpan.FromDays(400));
        store.PurgeHybridMemory();
        Check(store.CurrentMemory(updated.MemoryId) is { IsActive: true }
            && ReadScalar(fixture, "SELECT COUNT(*) FROM live_transcripts WHERE conversation_id='" + current.ConversationId + "'") == "2",
            "manual-only history survives every former retention deadline");
        var restarted = new EveningMemoryStore(fixture.Configuration, fixture.Journal, fixture.Clock);
        Check(restarted.LocalMemory(returning, MemoryScope.Personal).Single().MemoryId == fact.MemoryId,
            "returning guest local recall survives backend restart");

        var beforeDelete = store.MemoryDeletionEpochFor(current.DeviceId, current.ConversationId);
        Check(store.DeleteScopedMemory(current.DeviceId, "conversation", current.ConversationId, store.MemoryEpoch,
            out var affected) && affected.Contains(current.ConversationId), "scoped deletion returns media invalidation identities");
        Check(store.MemoryDeletionEpochFor("another-device", "another-conversation") == unrelatedDeletion,
            "deleting one guest never invalidates an unrelated device conversation");
        Check(!store.AppendTimedTranscripts(current, "late-generation", [new("late", "user", "Late secret", 5, 6, "late-event")], beforeDelete)
            && !store.Append(current.DeviceId, current.EveningId, "late-op", current.ConversationId, "Late secret", "late", beforeDelete),
            "deletion generation atomically fences late transcript and archive writes");
        Check(store.WriteMemory(current, new(Guid.NewGuid().ToString("D"), MemoryScope.Conversation,
            MemoryKind.UserReportedFact, "Late derived secret", "late-derived", ExpectedDeletionEpoch: store.MemoryDeletionEpochFor(current.DeviceId, current.ConversationId))) is null,
            "durable conversation tombstone blocks re-derivation even after generation refresh");
        Check(ReadScalar(fixture, "SELECT COUNT(*) FROM live_transcripts WHERE conversation_id='" + current.ConversationId + "'") == "0",
            "manual conversation deletion removes its finalized raw archive");

        service.ClearCache();
        handler.BeforeResult = () => store.DeleteScopedMemory(returning.DeviceId, "guest", guestId, store.MemoryEpoch);
        var deletedDuringSearch = await service.RetrieveAsync(returning, MemoryScope.Personal, "hodiny", CancellationToken.None);
        Check(deletedDuringSearch.Records.Count == 0 && store.CurrentMemory(fact.MemoryId) is { IsActive: false, Text: "" },
            "late search completion cannot republish manually deleted personal facts");
        var contextOnly = original with { ConversationId = Guid.NewGuid().ToString("D") };
        await service.StartupContextAsync(contextOnly, CancellationToken.None);
        var contextOnlyEpoch = store.MemoryDeletionEpochFor(contextOnly.DeviceId, contextOnly.ConversationId);
        Check(store.DeleteScopedMemory(contextOnly.DeviceId, "evening", contextOnly.EveningId, store.MemoryEpoch,
            out var eveningContexts) && eveningContexts.Contains(contextOnly.ConversationId)
            && store.MemoryDeletionEpochFor(contextOnly.DeviceId, contextOnly.ConversationId) != contextOnlyEpoch,
            "evening deletion fences registered provider contexts before their first finalized transcript");
    }

    private static async Task VerifyQueuedMemoryMutationFencesAsync()
    {
        using var fixture = new Fixture();
        var access = fixture.Access;
        access = access with { Guest = fixture.Store.SetGuestPermission(access.DeviceId, access.ConversationId,
            "queued-host", "operator-explicit-binding", true, true, false, 0) };
        var original = fixture.Store.WriteMemory(access, new(Guid.NewGuid().ToString("D"), MemoryScope.Personal,
            MemoryKind.UserReportedFact, "Původní vzpomínka před odstraněním.", "source:before-delete"))!;
        using var provider = new OpenAiVectorMemoryProvider(new HttpClient(new SearchFixture()), null, fixture.Options);
        var service = new HybridMemoryService(fixture.Store, provider, fixture.Options, fixture.Clock);
        var writes = (SemaphoreSlim)typeof(HybridMemoryService)
            .GetField("localWrites", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(service)!;
        await writes.WaitAsync();
        Task<MemoryRetrieval> remember;
        try
        {
            remember = service.ChangePersonalMemoryAsync(access, LiveRequestIntent.Remember,
                new("source:queued-save", "Zapamatuj si pozdní tajemství."), CancellationToken.None,
                expectedFactEpoch: service.StoreEpochFor(access),
                expectedDeletionEpoch: fixture.Store.MemoryDeletionEpochFor(access.DeviceId, access.ConversationId));
            Check(!remember.IsCompleted, "remember operation is deterministically queued behind a prior local writer");
            Check(fixture.Store.DeleteMemory(access.DeviceId, original.MemoryId, original.Revision), "record deleted while remember is queued");
        }
        finally { writes.Release(); }
        Check((await remember).Status == "memory_write_unavailable"
            && fixture.Store.LocalMemory(access, MemoryScope.Personal).Count == 0,
            "queued remember cannot rebase accepted input onto a post-deletion generation");

        await writes.WaitAsync();
        Task<MemoryRetrieval> forget;
        MemoryRecord reboundFact;
        MemoryAccess rebound;
        try
        {
            forget = service.ChangePersonalMemoryAsync(access, LiveRequestIntent.Forget,
                new("source:queued-forget", "Zapomeň moje vzpomínky."), CancellationToken.None,
                expectedFactEpoch: service.StoreEpochFor(access),
                expectedDeletionEpoch: fixture.Store.MemoryDeletionEpochFor(access.DeviceId, access.ConversationId));
            Check(!forget.IsCompleted, "forget operation is deterministically queued");
            rebound = access with { Guest = fixture.Store.SetGuestPermission(access.DeviceId, access.ConversationId,
                "rebound-host", "operator-new-binding", true, true, false, access.Guest!.Version) };
            reboundFact = fixture.Store.WriteMemory(rebound, new(Guid.NewGuid().ToString("D"), MemoryScope.Personal,
                MemoryKind.UserReportedFact, "Soukromí nově přiřazeného hosta.", "source:rebound"))!;
        }
        finally { writes.Release(); }
        Check((await forget).Status == "memory_write_unavailable"
            && fixture.Store.LocalMemory(rebound, MemoryScope.Personal).Single().MemoryId == reboundFact.MemoryId,
            "queued forget cannot delete a guest rebound after its source input was accepted");
    }

    private static async Task VerifyPermittedContinuityAsync()
    {
        using var fixture = new Fixture();
        var access = fixture.Access;
        const string guestId = "host-042";
        access = access with { Guest = fixture.Store.SetGuestPermission(access.DeviceId, access.ConversationId,
            guestId, "operator-explicit-continuity", true, true, false, 0) };
        using var provider = new OpenAiVectorMemoryProvider(new HttpClient(new SearchFixture()), null, fixture.Options);
        var service = new HybridMemoryService(fixture.Store, provider, fixture.Options, fixture.Clock);
        await service.PersistContextAsync(access, "current-generation", [],
            [new("user:ordinary-answer", "Ve volném čase pěstuju orchideje.")], service.StoreEpochFor(access), CancellationToken.None);
        var returning = access with { ConversationId = Guid.NewGuid().ToString("D"), Guest = null };
        returning = returning with { Guest = fixture.Store.SetGuestPermission(returning.DeviceId, returning.ConversationId,
            guestId, "operator-verified-return", true, true, false, 0) };
        Check((await service.StartupContextAsync(returning, CancellationToken.None)).Text.Contains("orchideje", StringComparison.Ordinal),
            "explicit opaque host-042 binding retains ordinary finalized answers across the durable returning-context boundary");
        var differentCase = access with { ConversationId = Guid.NewGuid().ToString("D"), Guest = null };
        differentCase = differentCase with { Guest = fixture.Store.SetGuestPermission(differentCase.DeviceId, differentCase.ConversationId,
            "HOST-042", "operator-distinct-label", true, true, false, 0) };
        Check(!(await service.StartupContextAsync(differentCase, CancellationToken.None)).Text.Contains("orchideje", StringComparison.Ordinal),
            "opaque stable identity labels remain exact and never acquire case aliases");
        var uppercaseUuid = Guid.NewGuid().ToString("D").ToUpperInvariant();
        var uuidBinding = fixture.Store.SetGuestPermission(access.DeviceId, Guid.NewGuid().ToString("D"), uppercaseUuid,
            "operator-existing-uuid", true, true, false, 0)!;
        Check(uuidBinding.GuestId == uppercaseUuid, "existing UUID identities preserve their exact representation");
        foreach (var invalid in new[] { "host 042", "host/042", "host:042", "žluťoučký", new string('a', 81) })
        {
            ExpectInvalidBinding(fixture.Store, access.DeviceId, invalid, "operator-source");
        }
        ExpectInvalidBinding(fixture.Store, access.DeviceId, "valid-label", "operator\nforged-source");
        var deletionEpoch = fixture.Store.MemoryDeletionEpochFor(access.DeviceId, access.ConversationId);
        var outcomeId = Guid.NewGuid().ToString("D");
        var operationId = Guid.NewGuid().ToString("D");
        Check(service.RecordConfirmedOutcome(access, operationId, outcomeId, "Testovací hůlka", "completed_by_timing", deletionEpoch)
            && service.RecordConfirmedOutcome(access, operationId, outcomeId, "Testovací hůlka", "completed_by_timing", deletionEpoch),
            "validated timed outcome receipts create idempotent scoped confirmed memory");
        var confirmed = fixture.Store.LocalMemory(access).Where(record => record.Kind == MemoryKind.ConfirmedSelection).ToArray();
        Check(confirmed.Length == 2 && confirmed.All(record => record.Text.Contains("Fyzická poloha nebyla změřena", StringComparison.Ordinal)),
            "confirmed selection records distinguish a timed sequence from measured physical delivery");
        service.InvalidateCurrentFacts(access);
        Check(fixture.Store.MemoryDeletionEpochFor(access.DeviceId, access.ConversationId) == deletionEpoch
            && fixture.Store.LocalMemory(returning, MemoryScope.Personal).All(record => record.Kind != MemoryKind.UserReportedFact),
            "correction supersedes this origin personal facts without terminating its provider generation");
        Check(ReadScalar(fixture, "SELECT COUNT(*) FROM memory_records WHERE text LIKE '%orchideje%'") == "2",
            "superseded personal and current-session statement revisions remain archived until manual deletion");
        Check(fixture.Store.LocalMemory(returning, MemoryScope.Personal).Any(record => record.Kind == MemoryKind.ConfirmedSelection),
            "a guest correction cannot undo an authoritative completed sequence outcome");
        fixture.Store.DeleteScopedMemory(access.DeviceId, "conversation", access.ConversationId, fixture.Store.MemoryEpoch);
        Check(!service.RecordConfirmedOutcome(access, operationId, outcomeId, "Testovací hůlka", "completed_by_timing", deletionEpoch),
            "late authoritative outcome cannot resurrect content after explicit conversation deletion");
    }

    private static void ExpectInvalidBinding(EveningMemoryStore store, string device, string identity, string source)
    {
        try
        {
            store.SetGuestPermission(device, Guid.NewGuid().ToString("D"), identity, source, true, true, false, 0);
        }
        catch (ArgumentException) { return; }
        throw new Exception("Dependable memory: invalid opaque identity/source binding was accepted");
    }

    private static string ReadScalar(Fixture fixture, string sql)
    {
        using var database = new SqliteConnection($"Data Source={fixture.DatabasePath}.evening.sqlite;Pooling=False");
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static void Check(bool value, string message)
    {
        if (!value) { throw new Exception("Dependable memory: " + message); }
    }

    private sealed class SearchFixture : HttpMessageHandler
    {
        internal bool Fail;
        internal bool Timeout;
        internal Action? BeforeResult;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Fail) { throw new HttpRequestException("deterministic provider unavailable"); }
            if (Timeout) { throw new OperationCanceledException("deterministic provider timeout"); }
            BeforeResult?.Invoke();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "kufr-dependable-memory-" + Guid.NewGuid().ToString("N"));
        internal readonly AdjustableTimeProvider Clock = new(DateTimeOffset.Parse("2026-10-19T12:00:00Z"));
        internal readonly IConfiguration Configuration;
        internal readonly OperationJournal Journal;
        internal readonly EveningMemoryStore Store;
        internal readonly HybridMemoryOptions Options;
        internal readonly MemoryAccess Access;
        internal readonly string DatabasePath;
        internal Fixture()
        {
            Directory.CreateDirectory(directory);
            DatabasePath = Path.Combine(directory, "journal.sqlite");
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:DatabasePath"] = DatabasePath,
                ["Backend:Memory:Enabled"] = "true",
                ["Backend:Memory:CloudIndexingEnabled"] = "true",
                ["Backend:Memory:LongTermPersonalEnabled"] = "true",
                ["Backend:Memory:PersonalStoreId"] = "vs_personal",
                ["Backend:Memory:MotifStoreId"] = "vs_motifs",
                ["Backend:Memory:Environment"] = "Development"
            }).Build();
            Journal = new(Configuration, Clock);
            Store = new(Configuration, Journal, Clock);
            Options = HybridMemoryOptions.Read(Configuration);
            Access = new("dependable-device", Store.GetStatus("dependable-device").EveningId, Guid.NewGuid().ToString("D"), "dv01");
        }
        public void Dispose()
        {
            Journal.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
