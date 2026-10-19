using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Realtime;
using Microsoft.Extensions.Configuration;

internal static class ConversationOrchestrationTests
{
    internal static void Run()
    {
        foreach (var gptLive in new[] { false, true })
        {
            var directory = Path.Combine(Path.GetTempPath(), "kufr-context-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                VerifyEngine(directory, gptLive);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                Directory.Delete(directory, true);
            }
        }
    }

    private static void VerifyEngine(string directory, bool gptLive)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = Path.Combine(directory, "journal.sqlite")
        }).Build();
        using var journal = new OperationJournal(configuration, TimeProvider.System);
        var memory = new EveningMemoryStore(configuration, journal);
        var store = new ConversationStore(TimeProvider.System, journal, eveningMemory: memory,
            liveOptions: new() { Mode = LiveActorMode.AutonomousActor });
        var settings = store.ReadServerModes();
        Require(store.ChangeServerModes(new(settings.Revision, PersonalityId: PersonalityCatalog.OllivanderId,
            CharacterEnabled: true)).Status == 200, "DV01 enabled without retired provider flags");
        var boot = Guid.NewGuid();
        var created = store.Create("context-device", Guid.NewGuid(), "guest-a"u8,
            new("context-device", boot, 0, PartyWandCatalog.Version, "cs-CZ", new(true, false))).Response!;
        var request = new RealtimeTurnRequest(Guid.NewGuid(), boot, 0, 63, 1,
            PartyWandCatalog.Version, true, "", null, "marin", 1);
        using var lifetime = new CancellationTokenSource();
        var call = store.BeginRealtimeTurn("context-device", created.ConversationId, Guid.NewGuid(), "admit"u8.ToArray(),
            request, true, lifetime.Token, default, handsFree: true, gptLive: gptLive).Call
            ?? throw new Exception("retained engine admission failed");
        Require(call.Context.LivePrompt?.Version == "dv01-adult-v1",
            "both retained engines admit the actual character without legacy providers");
        Require(store.RegisterRealtimeLifetime(call!, lifetime), "first provider stream owns its lifetime");
        foreach (var (text, index) in new[] { "I collect purple clocks.", "I prefer quiet rooms.", "My name is Ada.", "Correction: my name is Eva." }.Select((text, index) => (text, index)))
        {
            Require(store.AppendRealtimeTranscript(call, "final-" + index, text, "Understood " + index), "final event is accepted");
        }
        Require(store.AppendRealtimeTranscript(call, "final-0", "duplicate", "duplicate"), "duplicate final event is idempotent");
        Require(memory.GetStatus("context-device").ExchangeCount == 4,
            "DV01 finalized events persist incrementally once, before terminal completion");
        Require(store.CompleteRealtimeTurn(call, new("", "One more question?", "ask", "", null, 0)) is not null,
            "shared completion publishes the turn");
        store.ReleaseRealtimeTurn(call, true, true);
        Require(!store.AppendRealtimeTranscript(call, "after-release", "obsolete output", "late reply"),
            "a released provider operation cannot append into the next turn of the same conversation");
        Require(store.BeginRealtimeTurn("context-device", created.ConversationId, Guid.NewGuid(), "duplicate-start"u8.ToArray(),
            request with { ClientTurnId = Guid.NewGuid(), Opening = false }, true, default, default,
            handsFree: true, gptLive: !gptLive).Code == "conversation_stream_active",
            "rapid engine switching cannot start a second stream during playback acknowledgement");
        var next = store.BeginRealtimeTurn("context-device", created.ConversationId, Guid.NewGuid(), "follow-up"u8.ToArray(),
            request with { ClientTurnId = Guid.NewGuid(), Opening = false }, true, lifetime.Token, default,
            handsFree: true, gptLive: gptLive).Call!;
        Require(next.Context.History.Count == 4 && next.Context.History[0].VisitorText.Contains("purple clocks")
            && next.Context.History[^1].VisitorText.Contains("Correction"),
            "history preserves an early answer beyond two exchanges and its later correction");
        store.EndConversation("context-device", created.ConversationId, Guid.NewGuid());
        Require(!store.AppendRealtimeTranscript(next, "late-a", "A secret", "late reply"),
            "late finalized work cannot enter the ended guest archive or replacement context");
        store.ReleaseRealtimeTurn(next, true, false);
        var replacement = store.Create("context-device", Guid.NewGuid(), "guest-b"u8,
            new("context-device", boot, 0, PartyWandCatalog.Version, "cs-CZ", new(true, false))).Response!;
        var newGuest = store.BeginRealtimeTurn("context-device", replacement.ConversationId, Guid.NewGuid(), "new-admit"u8.ToArray(),
            request with { ClientTurnId = Guid.NewGuid() }, true, default, default,
            handsFree: true, gptLive: gptLive).Call!;
        Require(newGuest.Context.History.Count == 0 && !newGuest.Context.EveningContext.Contains("purple clocks"),
            "a new unbound guest starts without the previous guest personal context");
        store.EndConversation("context-device", replacement.ConversationId, Guid.NewGuid());
        store.ReleaseRealtimeTurn(newGuest, true, false);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new Exception(message); }
    }
}
