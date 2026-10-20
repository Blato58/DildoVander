using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Realtime;
using Kufr.Backend.Features.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;

internal static class VisualResponseTests
{
    internal static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kufr-visual-publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Backend:DatabasePath"] = Path.Combine(directory, "journal.sqlite") }).Build();
            using var journal = new OperationJournal(configuration, TimeProvider.System);
            var archive = new EveningMemoryStore(configuration, journal, TimeProvider.System);
            var memoryOptions = HybridMemoryOptions.Read(configuration);
            using var vector = new OpenAiVectorMemoryProvider(new HttpClient(), null, memoryOptions);
            var memory = new HybridMemoryService(archive, vector, memoryOptions, TimeProvider.System);
            var store = new ConversationStore(TimeProvider.System, journal, eveningMemory: archive);
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var boot = Guid.NewGuid();
            var id = store.Create("device", Guid.NewGuid(), "create"u8,
                new("device", boot, 0, PartyWandCatalog.Version, "cs-CZ", new(true, true))).Response!.ConversationId;
            var request = new RealtimeTurnRequest(Guid.NewGuid(), boot, 0, 63, 7, PartyWandCatalog.Version, true, "", null, "marin", 1);
            var call = store.BeginRealtimeTurn("device", id, Guid.NewGuid(), "turn"u8.ToArray(), request, true,
                lifetime.Token, default, handsFree: true).Call!;
            Check(store.StartRealtimeTurn(call), "actual operation starts");
            var access = store.LiveMemoryAccess(call)!;
            foreach (var item in new[] { (MemoryKind.UserReportedFact, "literal clock repair"),
                (MemoryKind.VisualObservation, "revoked photo cloak"), (MemoryKind.ModelInterpretation, "inferred photo story") })
            {
                Check(archive.WriteMemory(access, new(Guid.NewGuid().ToString("D"), MemoryScope.Conversation,
                    item.Item1, item.Item2, "fixture:recovery")) is not null, "recovery fixture record is canonical");
            }
            var recovery = memory.RecoveryContext(access).Text;
            Check(recovery.Contains("literal clock repair") && !recovery.Contains("revoked photo cloak") && !recovery.Contains("inferred photo story"),
                "provider recovery removes canonical conversation visual and inferred records as well as assistant history");
            using var session = new RealtimeLiveSession("device", id, new Input(), lifetime);
            using var writer = new SemaphoreSlim(0, 1);
            var commits = 0; var publications = 0;
            var stale = VisualResponsePublisher.PublishAsync(store, session, writer, 0, 0,
                () => { commits++; return store.CompleteRealtimeTurn(call, new("", "obsolete photograph", "ask", "", null, 1000))!; },
                (_, _) => { publications++; return Task.CompletedTask; }, lifetime.Token);
            var visual = store.ReadVisualResponseRevision("device", id);
            var correlation = new CostumeImageUpload(Guid.NewGuid(), boot, 0, 7, PartyWandCatalog.Version, []) { Revision = 1 };
            Check(store.DiscardVisualRevision("device", id, correlation).Status == 204 && visual.Changed.IsCompleted,
                "revoke signals active provider without deleting conversation");
            session.CancelOutput(); writer.Release();
            try { await stale; throw new Exception("Stale result published"); } catch (VisualResponseRevokedException) { }
            Check(commits == 0 && publications == 0 && store.IsRealtimeTurnCurrent(call),
                "revoke while completion waits for writer prevents domain commit and stale completed publication");
            await VisualResponsePublisher.PublishAsync(store, session, writer, 1, 1,
                () => { commits++; return store.CompleteRealtimeTurn(call, new("", "safe continuation", "ask", "", null, 1000))!; },
                (_, _) => { publications++; return Task.CompletedTask; }, lifetime.Token);
            Check(commits == 1 && publications == 1, "same application operation can complete a fresh validated response");
            var oldPlayback = session.PreparePlayback(call.OperationId);
            session.CancelOutput();
            Check(oldPlayback.IsCompleted && session.Played(call.OperationId, 1), "retired playback ACK remains idempotent");
            var freshPlayback = session.PreparePlayback(call.OperationId);
            Check(session.Played(call.OperationId, 1) && !freshPlayback.IsCompleted && !session.Played(call.OperationId),
                "old or unversioned ACK cannot acknowledge fresh same-operation audio");
            Check(session.Played(call.OperationId, 2) && freshPlayback.IsCompleted, "fresh generation ACK drains current audio");
            session.Listen(call);
            Check(await session.UploadAsync(request.ClientTurnId, 0, new byte[2], lifetime.Token), "first input accepted");
            session.Listen(call);
            Check(await session.UploadAsync(request.ClientTurnId, 1, new byte[2], lifetime.Token), "same active listening identity preserves PCM ordering");
            var first = store.ReadVisualResponseRevision("device", id);
            Check(store.DiscardVisualRevision("device", id, correlation).Status == 204
                && store.ReadVisualResponseRevision("device", id).Generation == first.Generation + 1,
                "idempotent DELETE retry emits a fresh causal visual playback barrier");
            store.ReleaseRealtimeTurn(call, true, true);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
    private sealed class Input : IStreamingAudioInput
    { public Task SendAudioAsync(byte[] pcm, CancellationToken token) => Task.CompletedTask; }
    private static void Check(bool condition, string message)
    { if (!condition) { throw new Exception("Visual response: " + message); } }
}
