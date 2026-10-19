using System.Diagnostics;
using Kufr.Backend.Features.Character;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Realtime;
using Kufr.Backend.Features.VoiceTurns;
using Microsoft.Extensions.Configuration;

internal static class CharacterOperationTests
{
    internal static void Run()
    {
        VerifyPublicationAndSnapshot();
        MeasureOverhead();
        Console.WriteLine("DV01 snapshot/publication/anonymous history tests passed.");
    }

    private static void VerifyPublicationAndSnapshot()
    {
        foreach (var profile in new[] { FinaleProfile.Blocked, FinaleProfile.Limited })
        {
            var directory = Path.Combine(Path.GetTempPath(), "dv01-publication-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["Backend:DatabasePath"] = Path.Combine(directory, "journal.sqlite") }).Build();
                using var journal = new OperationJournal(configuration, TimeProvider.System);
                using var dialog = new TextDialogRunner((_, _, _, _, _) => throw new Exception("Legacy Sol called"));
                using var voice = new VoiceProviderRunner((_, _) => throw new Exception("STT called"), (_, _) => throw new Exception("TTS called"));
                var options = new GptLiveOptions { Mode = LiveActorMode.AutonomousActor };
                var store = new ConversationStore(TimeProvider.System, journal, dialog, voiceRunner: voice, liveOptions: options);
                var modes = store.ReadServerModes();
                var changed = store.ChangeServerModes(new(modes.Revision, PersonalityId: PersonalityCatalog.OllivanderId,
                    CharacterEnabled: true, CharacterTone: "Chaos", CharacterFinaleProfile: profile.ToString()));
                Require(changed.Status == 200, "admin explicit profile accepted before start");
                var enabledModes = store.ReadServerModes();
                var incompatible = store.ChangeServerModes(new(enabledModes.Revision, PersonalityId: PersonalityCatalog.DefaultId));
                Require(incompatible.Status == 409 && incompatible.Error == "character_requires_autonomous_ollivander"
                    && store.ReadServerModes().PersonalityId == PersonalityCatalog.OllivanderId
                    && store.ReadServerModes().Revision == enabledModes.Revision,
                    "ordinary personality-only change cannot silently leave enabled DV01 on another persona");
                var boot = Guid.NewGuid(); var device = "synthetic-device";
                var created = store.Create(device, Guid.NewGuid(), "create"u8,
                    new(device, boot, 0, PartyWandCatalog.Version, "cs-CZ", new(true, false))).Response!;
                var active = store.ReadServerModes();
                Require(store.ChangeServerModes(new(active.Revision, CharacterEnabled: false)).Status == 200,
                    "ordinary disabling applies to next session even with active conversation");
                var request = new RealtimeTurnRequest(Guid.NewGuid(), boot, 0, 63, 7, PartyWandCatalog.Version, true, "", null, "marin", 1);
                var admission = store.BeginRealtimeTurn(device, created.ConversationId, Guid.NewGuid(), "call"u8.ToArray(),
                    request, true, CancellationToken.None, CancellationToken.None, handsFree: true, gptLive: true);
                var call = admission.Call ?? throw new Exception(admission.Code);
                Require(call.Context.LivePrompt?.Version == "dv01-adult-v1" && call.Context.LivePrompt.Options.Character.Enabled
                    && call.Context.LivePrompt.Options.Character.HostTone == CharacterTone.Chaos, "active snapshot survives feature disable");
                Require(store.StartRealtimeTurn(call), "call starts once");
                store.RecordCharacterStyle(call, "old guest private text");
                Require(store.CharacterStyles(call).Count == 0, "personal strings cannot enter anonymous history");
                for (var i = 0; i < 30; i++) { store.RecordCharacterStyle(call, "bureaucracy"); }
                Require(store.CharacterStyles(call).Count == 12, "anonymous scope is bounded");
                var latest = store.ReadServerModes();
                Require(store.ChangeServerModes(new(latest.Revision, ResetCharacterVariety: true)).Status == 200
                    && store.CharacterStyles(call).Count == 0, "variety reset has no operation effects");
                var decision = new WandDecisionResult(WandDecisionStatus.ValidProposal, 1, [], "", 2, 7);
                var outcome = new LiveOutcome(new("synthetic input", "Výběr mám.", "propose", "", 1, 10), 2, [])
                { Autonomous = true, PlaybackVerified = true, Decision = decision, DecisionRevision = 2 };
                Require(store.CompleteGptLive(call, outcome with { PlaybackVerified = false }) is null, "no publication without drain");
                Require(store.CompleteGptLive(call with { Context = call.Context with { Request = request with { CatalogVersion = "other-catalog" } } }, outcome) is null,
                    "changed catalog cannot publish against a frozen character snapshot");
                var result = store.CompleteGptLive(call, outcome)!;
                Require(profile == FinaleProfile.Blocked ? result.Proposal is null && result.Decision == "no_selection"
                    : result.Proposal is SelectionProposal { Compartment: 1, DeviceRevision: 7 }, "physical proposal follows frozen explicit profile");
                Require(store.CompleteGptLive(call, outcome) is null, "duplicate completion cannot publish second proposal");
                store.ReleaseRealtimeTurn(call, true, true); call.Cancellation.Dispose();
                store.EndConversation(device, created.ConversationId, Guid.NewGuid());
                Require(store.CompleteGptLive(call, outcome) is null, "closed session cannot be revived by late decision");
            }
            finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
        }
        try
        {
            CharacterConfiguration.Read(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["FinaleProfile"] = "Named" }).Build());
            throw new Exception("Unavailable named profile accepted");
        }
        catch (InvalidOperationException) { }
    }

    private static void MeasureOverhead()
    {
        var session = new CharacterSession("overhead-fixture", new(true));
        session.AcceptInput("a", 1, "Ztrácím klíče denně");
        for (var i = 0; i < 100; i++) { CharacterDirector.Evaluate(session, CharacterPhase.Ready, 2); }
        var samples = new double[10000];
        for (var i = 0; i < samples.Length; i++)
        {
            var started = Stopwatch.GetTimestamp();
            CharacterDirector.Evaluate(session, CharacterPhase.Ready, 2);
            samples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        Array.Sort(samples);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        { metric = "director_overhead", iterations = samples.Length, medianMs = samples[5000], p95Ms = samples[9500], maximumMs = samples[^1], source = "synthetic local CPU, no Live latency" }));
    }
    private static void Require(bool result, string message) { if (!result) { throw new Exception("DV01: " + message); } }
}
