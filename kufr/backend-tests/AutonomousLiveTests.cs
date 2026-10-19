using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Realtime;
using Microsoft.Extensions.Configuration;
using Kufr.Backend.Features.Character;
using Kufr.Backend.Features.Show;
using Microsoft.Data.Sqlite;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.VoiceTurns;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

internal static class AutonomousLiveTests
{
    internal static async Task RunCharacterAsync()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:GptLive:ActorMode"] = "AutonomousActor", ["Backend:GptLive:Character:Enabled"] = "true"
        }).Build();
        var options = GptLiveOptions.Read(configuration) with { DelegationContextWaitMs = 0, OutputPauseMs = 500, TerminalQuietMs = 1000 };
        Check(options.Character.Enabled && options.Character.HostTone == CharacterTone.Adult
            && options.Character.FinaleProfile == FinaleProfile.Blocked, "opt-in adult and explicit blocked finale");
        var candidate = LivePromptSnapshot.Create(PersonalityCatalog.OllivanderId, options);
        var legacy = LivePromptSnapshot.Create(PersonalityCatalog.OllivanderId, options with { Character = new() });
        Check(candidate.Version == "dv01-adult-v1" && legacy.Version == "autonomous-live-v3"
            && candidate.Sha256 != legacy.Sha256, "separate version leaves feature-off snapshot intact");
        Check(!AutonomousLiveDialogue.LimitedTerminalReady("Poslední slovo a Knihovní bouře.")
            && AutonomousLiveDialogue.LimitedTerminalReady("Výběr mám. Mechanika mi nepotvrdila konec, zatím na to nesahej."),
            "limited publication rejects catalog identity, remains distinct from named");
        foreach (var speech in new[] { "První přihrádka.", "V první přihrádce.", "Přihrádka šest.",
            "Přihrádka číslo ŠEST.", "Druhou přihrádku.", "Slot 6.", "Šestý slot.", "Přihrádka 7.",
            "Ze sedmi přihrádek.", "Přihrádka jednička.", "Přihrádka číslo 99.",
            "Přihrádka: šest.", "Přihrádka č. 6.", "Slot číslo: dva.", "V osmé přihrádce." })
        { Check(!AutonomousLiveDialogue.LimitedTerminalReady(speech), "limited rejects spoken slot: " + speech); }
        foreach (var speech in new[] { "První dojem.", "Šest nápadů.", "Přihrádka zůstává zavřená.",
            "Přihrádka je patřičně zavřená." })
        { Check(AutonomousLiveDialogue.LimitedTerminalReady(speech), "unrelated number is not a slot: " + speech); }
        VerifyCharacterControlEvidence(options);
        foreach (var control in new[] { "Zrychli.", "Bez dalších otázek.", "Vyber rovnou." })
        { await VerifyExpressWithoutEvidence(options, control); }
        await VerifyExpressAfterNeedsInformation(options);
        foreach (var profile in new[] { FinaleProfile.Blocked, FinaleProfile.Limited })
        {
            await VerifyCharacterFlow(options with { Character = options.Character with { FinaleProfile = profile } });
        }
        await VerifyCharacterFlow(options, interrupted: true);
        await VerifyCharacterFlow(options, express: true);
        await VerifyConversation(options, correction: true);
        await VerifyCharacterCancellation(options);
        await VerifySplitVoiceControl(options);
        await VerifyPcmCancellationFence(options);
        foreach (var control in new[] { "To řekl kamarád.", "To není pravda.", "To byl vtip." })
        { await VerifyCharacterEvidenceWithdrawal(options, control); }
        await VerifyCharacterEvidenceWithdrawal(options, "To řekl kamarád.", sameReference: true);
        await VerifyOperatorOverrideInvalidatedByCorrection(options with
        { Character = options.Character with { FinaleProfile = FinaleProfile.Limited } });
        foreach (var clipOutcome in new[] { "completed", "expired", "stopped", "thanks", "repeat", "empty" })
        { await VerifyLocalFinaleClipBoundary(options with
            { Character = options.Character with { FinaleProfile = FinaleProfile.Limited } }, clipOutcome); }
        Console.WriteLine("DV01 fake Live integration passed (no provider/audio hardware).");
    }

    private static async Task VerifyOperatorOverrideInvalidatedByCorrection(GptLiveOptions options)
    {
        var directory = Path.Combine(Path.GetTempPath(), "live-director-override-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var clock = new ManualClock();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:DatabasePath"] = Path.Combine(directory, "journal.sqlite"),
                ["Devices:0:DeviceId"] = "device"
            }).Build();
            using var journal = new OperationJournal(configuration, clock);
            var memory = new EveningMemoryStore(configuration, journal, clock);
            var show = new ShowCoordinator(configuration, journal, memory, clock);
            using var socket = new SocketFixture();
            socket.BeforeDelivery = () => clock.Advance(50);
            await using var connection = new GptLiveConnection(socket);
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var call = Call(options, lifetime) with { EveningId = memory.GetStatus("device").EveningId };
            using var player = new RealtimeLiveSession("device", call.Correlation.ConversationId,
                connection, lifetime, continuous: true);
            var firstStarted = new TaskCompletionSource<WandDecisionInput>(TaskCreationOptions.RunContinuationsAsynchronously);
            var refreshedStarted = new TaskCompletionSource<WandDecisionInput>(TaskCreationOptions.RunContinuationsAsynchronously);
            var staleResult = new TaskCompletionSource<WandDecisionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var correctionSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var decisionCalls = 0;
            using var decisions = new WandDecisionService((input, _) =>
            {
                if (Interlocked.Increment(ref decisionCalls) == 1)
                { firstStarted.TrySetResult(input); return staleResult.Task.WaitAsync(lifetime.Token); }
                Check(input.Evidence.Last().Text.Contains("improvizuji", StringComparison.Ordinal),
                    "fresh actual decision service receives corrected evidence after override invalidation");
                refreshedStarted.TrySetResult(input);
                return Task.FromResult(Proposal(input, 2));
            });
            var consumed = Channel.CreateUnbounded<string>();
            var chunks = Channel.CreateUnbounded<int>();
            var checkpoints = Channel.CreateUnbounded<LivePlaybackRequest>();
            var pcmBytes = 0;
            var run = new AutonomousLiveDialogue(connection, decisions, clock, show: show).RunAsync(call,
                (pcm, _) => { pcmBytes += pcm.Length; chunks.Writer.TryWrite(pcm.Length); return Task.CompletedTask; },
                (text, _) =>
                {
                    consumed.Writer.TryWrite(text);
                    if (text.Contains("improvizuji", StringComparison.Ordinal)) { correctionSeen.TrySetResult(); }
                    return Task.CompletedTask;
                }, _ => Task.CompletedTask,
                (checkpoint, _) =>
                {
                    var drain = player.PrepareCheckpoint(checkpoint);
                    checkpoints.Writer.TryWrite(checkpoint);
                    return drain;
                }, () => (63, 7, true), _ => Task.CompletedTask, lifetime.Token,
                cancelAudio: (_, _) => { player.CancelCheckpoint(); return Task.CompletedTask; });
            await socket.Command("session.instructions.append", lifetime.Token);
            socket.Transcript(false, "Co děláš, když se plán rozpadne?", 0, 1000);
            socket.Transcript(true, "Plánuji všechno.", 1100, 2000);
            await consumed.Reader.ReadAsync(lifetime.Token);
            socket.Delegate("director-inflight", 2000);
            clock.Advance(1300);
            await AdvanceUntilAsync(firstStarted.Task, run, clock, lifetime.Token);
            var oldInput = await firstStarted.Task;
            var current = show.Read("device");
            var accepted = show.Command("device", new("force-six", current.Revision, "override",
                call.Correlation.ConversationId, call.SessionGeneration, "6"));
            Check(accepted.Status == "accepted", "real coordinator accepts scoped prepublication override during in-flight decision");
            var express = show.Command("device", new("override-consumption-barrier", show.Read("device").Revision,
                "tempo", call.Correlation.ConversationId, call.SessionGeneration, "express"));
            Check(express.Status == "accepted", "ordered harmless style command supplies observable owner-consumption barrier");
            async Task WaitForConsumedControlsAsync()
            {
                while (true)
                {
                    var command = await socket.Command("session.instructions.append", lifetime.Token);
                    using var document = JsonDocument.Parse(command);
                    var content = document.RootElement.GetProperty("content").GetString()!;
                    if (content.Contains("fáze=Responding", StringComparison.Ordinal)
                        && content.Contains("tempo=Express", StringComparison.Ordinal)) { return; }
                }
            }
            var controlsConsumed = WaitForConsumedControlsAsync();
            await AdvanceUntilAsync(controlsConsumed, run, clock, lifetime.Token);
            // Keep device revision7: changing that physical correlation correctly
            // requires a new turn. This scenario changes the guest evidence revision.
            socket.Transcript(true, " Oprava, improvizuji.", 2100, 2600);
            await correctionSeen.Task.WaitAsync(lifetime.Token);
            staleResult.SetResult(Proposal(oldInput, 1));
            await AdvanceUntilAsync(refreshedStarted.Task, run, clock, lifetime.Token);
            var freshInput = await refreshedStarted.Task;
            var commentary = socket.Command("session.commentary.append", lifetime.Token);
            await AdvanceUntilAsync(commentary, run, clock, lifetime.Token);
            await commentary;
            Check(freshInput.Revision > oldInput.Revision && freshInput.AvailabilityRevision == 7,
                "corrected evidence keeps the authoritative current physical snapshot");
            socket.Transcript(false, "Výběr mám. Mechanika není potvrzená. Tohle bude slavnostní průšvih.", 3000, 4000);
            socket.Audio();
            await chunks.Reader.ReadAsync(lifetime.Token);
            clock.Advance(2000);
            var checkpointRead = checkpoints.Reader.ReadAsync(lifetime.Token).AsTask();
            await AdvanceUntilAsync(checkpointRead, run, clock, lifetime.Token);
            var checkpoint = await checkpointRead;
            Check(checkpoint.DecisionRevision == freshInput.Revision && checkpoint.PcmBytes == pcmBytes && pcmBytes > 0,
                "single player checkpoint correlates only the refreshed decision and submitted audio");
            Check(!player.ConfirmCheckpoint(Guid.NewGuid(), checkpoint.PcmBytes, true)
                && !player.ConfirmCheckpoint(checkpoint.PlaybackId, checkpoint.PcmBytes + 2, true)
                && !player.ConfirmCheckpoint(checkpoint.PlaybackId, checkpoint.PcmBytes, false),
                "wrong identity, wrong byte count and undrained audio cannot publish an override result");
            clock.Advance(1000);
            await Task.Delay(1, lifetime.Token);
            Check(!run.IsCompleted && !socket.Sent.Any(s => s.Contains("session.close", StringComparison.Ordinal)),
                "provider append acknowledgment never substitutes for correlated player drain");
            Check(player.ConfirmCheckpoint(checkpoint.PlaybackId, checkpoint.PcmBytes, true), "exact local player drain accepted");
            await AdvanceUntilAsync(run, run, clock, lifetime.Token);
            var outcome = await run;
            Check(outcome.PlaybackVerified && outcome.Reply.Decision == "propose"
                && outcome.Decision is { Compartment: 2, AvailabilityRevision: 7 }
                && outcome.Decision.DecisionRevision == freshInput.Revision
                && outcome.Decision.OperationState != "operator_proposal_only" && decisionCalls == 2,
                "stale forced slot6 and old result1 are discarded; refreshed authoritative result2 is the sole proposal");
            Check(!checkpoints.Reader.TryRead(out _) && socket.Sent.Count(s => s.Contains("session.commentary.append", StringComparison.Ordinal)) == 1
                && socket.Sent.Count(s => s.Contains("session.close", StringComparison.Ordinal)) == 1,
                "one terminal commentary, one correlated drain and one close after override invalidation");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    private static async Task VerifyLocalFinaleClipBoundary(GptLiveOptions options, string clipOutcome)
    {
        var directory = Path.Combine(Path.GetTempPath(), "live-local-finale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var clock = new ManualClock();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:DatabasePath"] = Path.Combine(directory, "journal.sqlite"),
                ["Devices:0:DeviceId"] = "device", ["Devices:0:CredentialSha256"] = new string('a', 64),
                ["Keepsake:Enabled"] = "true", ["Keepsake:DataRoot"] = Path.Combine(directory, "archive"),
                ["Show:DeviceCueSpeechMode"] = "off"
            }).Build();
            using var journal = new OperationJournal(configuration, clock);
            var memory = new EveningMemoryStore(configuration, journal, clock);
            var show = new ShowCoordinator(configuration, journal, memory, clock);
            using var dialog = new TextDialogRunner((_, _, _, _, _) => throw new Exception("Local clip test must not call legacy Sol."));
            using var voice = new VoiceProviderRunner((_, _) => throw new Exception("Local clip test must not call STT."),
                (_, _) => throw new Exception("Local clip test must not call TTS."));
            var store = new ConversationStore(clock, journal, dialog, eveningMemory: memory,
                voiceRunner: voice, liveOptions: options, show: show);
            var modes = store.ReadServerModes();
            Check(store.ChangeServerModes(new(modes.Revision, "responses", "openai", PersonalityCatalog.OllivanderId,
                CharacterEnabled: true, CharacterTone: "Adult", CharacterFinaleProfile: "Limited")).Status == 200,
                "actual owner enables frozen limited character profile");
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var empty = clipOutcome == "empty";
            var boot = Guid.NewGuid();
            var created = store.Create("device", Guid.NewGuid(), "local-finale-create"u8,
                new("device", boot, 0, PartyWandCatalog.Version, "cs-CZ", new(true, false)));
            Check(created.Status == 201, "actual common conversation admission succeeds");
            var request = new RealtimeTurnRequest(Guid.NewGuid(), boot, 0, empty ? 0 : 63, 7,
                PartyWandCatalog.Version, true, "", null, "marin", 1);
            var admission = store.BeginRealtimeTurn("device", created.Response!.ConversationId, Guid.NewGuid(),
                "local-finale-call"u8.ToArray(), request, true, lifetime.Token, CancellationToken.None,
                handsFree: true, gptLive: true);
            var call = admission.Call ?? throw new Exception(admission.Code);
            Check(store.StartRealtimeTurn(call), "actual journal starts Live operation");
            var bridge = new ShowDeviceCueBridge(configuration, show, journal, voice, clock);
            ShowDeviceCueEnvelope Poll() => bridge.Poll("device", new(boot, 0, 7, clock.GetTimestamp(),
                show.Read("device").DeviceSettingsRevision, false, false, "marin"));
            ShowDeviceCueAck Ack(string outcome, string evidence) => new(boot, 0, 7, outcome, evidence);
            Poll();
            using var socket = new SocketFixture();
            socket.BeforeDelivery = () => clock.Advance(50);
            await using var connection = new GptLiveConnection(socket);
            using var player = new RealtimeLiveSession("device", call.Correlation.ConversationId,
                connection, lifetime, continuous: true);
            var decisionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var decisionCalls = 0;
            using var decisions = new WandDecisionService((input, _) =>
            {
                Check(!empty, "empty physical ready mask must not invoke a model decision");
                Interlocked.Increment(ref decisionCalls);
                decisionStarted.TrySetResult();
                return Task.FromResult(Proposal(input, 2));
            });
            var consumed = Channel.CreateUnbounded<string>();
            var chunks = Channel.CreateUnbounded<int>();
            var checkpoints = Channel.CreateUnbounded<LivePlaybackRequest>();
            var cancellations = 0;
            var run = new AutonomousLiveDialogue(connection, decisions, clock, show: show).RunAsync(call,
                (pcm, _) => { chunks.Writer.TryWrite(pcm.Length); return Task.CompletedTask; },
                (text, _) => { consumed.Writer.TryWrite(text); return Task.CompletedTask; },
                _ => Task.CompletedTask,
                (checkpoint, _) =>
                {
                    var drain = player.PrepareCheckpoint(checkpoint);
                    checkpoints.Writer.TryWrite(checkpoint);
                    return drain;
                }, () => store.LiveAvailability(call), _ => Task.CompletedTask, lifetime.Token,
                cancelAudio: (_, _) => { Interlocked.Increment(ref cancellations); player.CancelCheckpoint(); return Task.CompletedTask; });
            async Task WaitUntil(Func<bool> condition)
            {
                for (var tick = 0; tick < 500; tick++)
                {
                    if (condition()) { return; }
                    if (run.IsCompleted) { await run; throw new Exception("Live closed before the local-clip test boundary."); }
                    clock.Advance(50);
                    await Task.Delay(1, lifetime.Token);
                }
                throw new Exception("Local-clip test boundary was not reached.");
            }
            async Task<ShowDeviceCue> ReadNextCue()
            {
                ShowDeviceCue? cue = null;
                await WaitUntil(() => (cue = Poll().Cue) is not null);
                return cue!;
            }
            await socket.Command("session.instructions.append", lifetime.Token);
            if (empty)
            { await WaitUntil(() => show.Read("device").SceneExecution is not null); }
            else
            {
                socket.Transcript(false, "Co děláš, když se plán rozpadne?", 0, 1000);
                socket.Transcript(true, "Plánuji všechno.", 1100, 2000);
                await consumed.Reader.ReadAsync(lifetime.Token);
                socket.Delegate("local-finale", 2000);
                clock.Advance(1300);
                await AdvanceUntilAsync(decisionStarted.Task, run, clock, lifetime.Token);
                var commentary = socket.Command("session.commentary.append", lifetime.Token);
                await AdvanceUntilAsync(commentary, run, clock, lifetime.Token);
                await commentary;
            }
            // These are the actual compiled scene's finite non-speech cues.
            for (var index = 0; index < (empty ? 1 : 2); index++)
            {
                var cue = await ReadNextCue();
                Check(cue.CueType is "LedPreset" or "VibratePreset" && cue.Compartment == (empty ? 1 : 2),
                    "actual scene adapters retain selected compartment before terminal audio");
                Check(bridge.Acknowledge("device", cue.CueId, Ack("accepted", "commanded")).Status == 200
                    && bridge.Acknowledge("device", cue.CueId, Ack("completed", "commanded")).Status == 200,
                    "bounded local non-speech cues drain through actual mailbox");
            }
            socket.Transcript(false, empty ? "Žádná fyzická hůlka není dostupná. Nechám kufr odpočívat."
                : "Výběr mám. Mechanika není potvrzená. Tohle bude slavnostní průšvih.", 3000, 4000);
            socket.Audio();
            await chunks.Reader.ReadAsync(lifetime.Token);
            clock.Advance(2000);
            var checkpointRead = checkpoints.Reader.ReadAsync(lifetime.Token).AsTask();
            await AdvanceUntilAsync(checkpointRead, run, clock, lifetime.Token);
            var checkpoint = await checkpointRead;
            if (clipOutcome is "thanks" or "repeat")
            {
                var guest = clipOutcome == "thanks" ? "Díky." : "Zopakuj to.";
                socket.Transcript(true, guest, 4100, 4400);
                while ((await consumed.Reader.ReadAsync(lifetime.Token)) != guest) { }
                await WaitUntil(() => cancellations > 0);
                var resumedCommentary = socket.Command("session.commentary.append", lifetime.Token);
                clock.Advance(1300);
                await AdvanceUntilAsync(resumedCommentary, run, clock, lifetime.Token);
                await resumedCommentary;
                Check(!player.ConfirmCheckpoint(checkpoint.PlaybackId, checkpoint.PcmBytes, true)
                    && decisionCalls == 1 && show.Read("device").SceneExecution is { Cancelled: false },
                    "guest Thanks/Repeat invalidates the old player drain and rebuilds the retained finale without another decision");
                socket.Emit(new { type = "session.usage.updated", usage = new { seconds = 2 } });
                clock.Advance(1000);
                socket.Transcript(false, "Výběr platí. Mechanika není potvrzená. Slavnostní průšvih pokračuje.", 5000, 6000);
                socket.Audio();
                await chunks.Reader.ReadAsync(lifetime.Token);
                clock.Advance(2000);
                checkpointRead = checkpoints.Reader.ReadAsync(lifetime.Token).AsTask();
                await AdvanceUntilAsync(checkpointRead, run, clock, lifetime.Token);
                checkpoint = await checkpointRead;
            }
            Check(player.ConfirmCheckpoint(checkpoint.PlaybackId, checkpoint.PcmBytes, true), "primary terminal audio drain is exact");
            await WaitUntil(() => show.Read("device").SceneExecution?.Cues
                .Any(cue => cue.Kind == "PlayLocalClip" && cue.Status == "submitted") == true);
            // Let every authored pause elapse while the device has not polled
            // the ending clip. A primary player drain alone cannot close Live.
            clock.Advance(1300);
            await WaitUntil(() => show.Read("device").SceneExecution?.PausePending == false);
            Check(!run.IsCompleted && !socket.Sent.Any(value => value.Contains("session.close", StringComparison.Ordinal))
                && show.Read("device").SceneExecution?.PausePending == false,
                "closing waits for local clip independently of the expired stage pause");
            if (clipOutcome == "stopped")
            {
                var state = show.Read("device");
                Check(show.Command("device", new("stop-local-clip", state.Revision, "stop")).Status == "accepted", "operator Stop accepted during local clip wait");
                var cancelled = false;
                try { await AdvanceUntilAsync(run, run, clock, lifetime.Token); await run; }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && cancellations > 0 && !socket.Sent.Any(value => value.Contains("session.close", StringComparison.Ordinal))
                    && journal.ReadKeepsakeSelection("device", call.OperationId) is null,
                    $"Stop stays responsive during pending local clip and cannot publish a proposal (cancelled={cancelled}, audioCancellations={cancellations}, "
                    + $"closeCount={socket.Sent.Count(value => value.Contains("session.close", StringComparison.Ordinal))}, "
                    + $"proposal={journal.ReadKeepsakeSelection("device", call.OperationId) is not null}, phase={show.Read("device").Phase})");
                store.ReleaseRealtimeTurn(call, dispatched: true, completed: false);
                bridge.CancelAll();
                return;
            }
            if (clipOutcome != "expired")
            {
                var cue = await ReadNextCue();
                Check(cue.CueType == "PlayLocalClip" && cue.ClipId == "ending-1" && cue.AudioPath is null,
                    "actual post-primary ending uses approved local clip without TTS");
                Check(bridge.Acknowledge("device", cue.CueId, Ack("accepted", "commanded")).Status == 200,
                    "ending clip is admitted by actual device mailbox");
                clock.Advance(300);
                await WaitUntil(() => show.Read("device").SceneExecution?.Cues
                    .Any(item => item.Kind == "PlayLocalClip" && item.Status == "accepted") == true);
                Check(!run.IsCompleted && !socket.Sent.Any(value => value.Contains("session.close", StringComparison.Ordinal)),
                    "admitted clip remains pending until actual local player finish");
                Check(bridge.Acknowledge("device", cue.CueId, Ack("completed", "playback_finished")).Status == 200,
                    "local clip finishes with actual player evidence");
            }
            else { clock.Advance(8100); }
            await AdvanceUntilAsync(run, run, clock, lifetime.Token);
            var outcome = await run;
            Check(outcome.PlaybackVerified && socket.Sent.Count(value => value.Contains("session.close", StringComparison.Ordinal)) == 1
                && show.Read("device").SessionGeneration is null,
                "clip completion or bounded fallback closes once and releases Live visit");
            var result = store.CompleteGptLive(call, outcome);
            if (empty)
            {
                Check(result is { Decision: "no_selection", Proposal: null }
                    && outcome.Decision is { Status: WandDecisionStatus.NoAvailableWand, Compartment: null }
                    && decisionCalls == 0 && journal.ReadKeepsakeSelection("device", call.OperationId) is null
                    && show.ReadFinalePlan("device", call.OperationId) is null,
                    "verified initial empty-ready finale completes the actual owner with no selection or physical proposal");
                store.ReleaseRealtimeTurn(call, dispatched: true, completed: true);
                bridge.CancelAll();
                return;
            }
            var proposal = result?.Proposal as SelectionProposal;
            var plan = show.ReadFinalePlan("device", call.OperationId);
            Check(proposal is { Compartment: 2 } && plan?.ProposalId == proposal.ProposalId && plan.SessionGeneration == call.SessionGeneration
                && show.Read("device").SceneExecution?.Cues.Single(cue => cue.Kind == "PresentSelected").Status == "accepted",
                "actual returned Live result binds one original proposal after session disposal using retained scene generation");
            Check(decisionCalls == 1 && !checkpoints.Reader.TryRead(out _)
                && socket.Sent.Count(value => value.Contains("session.commentary.append", StringComparison.Ordinal))
                    == (clipOutcome is "thanks" or "repeat" ? 2 : 1),
                "retained-result recovery publishes exactly one proposal after the fresh correlated drain");
            if (clipOutcome == "expired")
            {
                Check(show.Read("device").SceneExecution?.Cues.Single(cue => cue.Kind == "PlayLocalClip").Status == "dropped",
                    "unpolled local ending has bounded explicit drop fallback");
            }
            else
            {
                var access = new DeviceAccessStore(configuration, clock);
                using var services = new ServiceCollection().AddSingleton(show).BuildServiceProvider();
                var context = new DefaultHttpContext { RequestServices = services };
                context.Request.Headers.Authorization = "Bearer " + access.IssueToken("device")!.AccessToken;
                context.Request.Headers["Idempotency-Key"] = Guid.NewGuid().ToString("D");
                var physical = new KeepsakeOutcomeEndpoints.OutcomeRequest(1, Guid.NewGuid().ToString("D"),
                    proposal!.ProposalId, Guid.NewGuid().ToString("D"), boot.ToString("D"), 0, 7, "completed_by_timing");
                var body = JsonSerializer.SerializeToUtf8Bytes(physical, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                context.Request.ContentType = "application/json";
                context.Request.ContentLength = body.Length;
                context.Request.Body = new MemoryStream(body);
                var keepsakes = new KeepsakeStore(configuration, clock);
                var published = await KeepsakeOutcomeEndpoints.PublishAsync(context, call.OperationId, access, journal, keepsakes, configuration, memory);
                Check((published as IStatusCodeHttpResult)?.StatusCode == 201
                    && show.ReadFinalePlan("device", call.OperationId) is { Locked: true, PresentationEvidence: "completed_by_timing" }
                    && show.Read("device").SceneExecution?.Cues.Single(cue => cue.Kind == "PresentSelected").Status == "completed"
                    && show.Read("device").SceneExecution?.Cues.Single(cue => cue.Kind == "WaitForEvent").Status == "completed",
                    "actual timing-outcome endpoint completes retained presentation cues without creating another physical command");
            }
            store.ReleaseRealtimeTurn(call, dispatched: true, completed: true);
            bridge.CancelAll();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    private static void VerifyCharacterControlEvidence(GptLiveOptions options)
    {
        foreach (var text in new[] { "To řekl kamarád.", "To není pravda.", "To byl vtip." })
        {
            var projection = new LiveConversationProjection(options);
            Append(projection, false, "Co chceš?", 0, 100);
            Append(projection, true, "Chci létat.", 200, 300);
            Append(projection, false, "Co dalšího?", 400, 500);
            Append(projection, true, text, 600, 700);
            projection.InvalidatePriorCharacterEvidence();
            projection.MarkLatestCharacterControl(invalidatesEvidence: true);
            Check(projection.Evidence().Count == 0 && projection.LatestCharacterControlInvalidatesEvidence
                && projection.AnswerCandidates == 1, "pure withdrawal is neither replacement evidence nor diagnostic answer");
            var controlReference = projection.LatestInputReference;
            Append(projection, true, " Oprava, chci přesnost.", 800, 900);
            projection.ClearLatestCharacterControl();
            Check(projection.Evidence().Count == 1 && projection.Evidence()[0].Text.Contains("přesnost")
                && !projection.LatestIsCharacterControl && !projection.LatestCharacterControlInvalidatesEvidence
                && projection.LatestInputReference == controlReference, "same-source substantive replacement remains evidence");
        }
    }

    private static async Task VerifyExpressWithoutEvidence(GptLiveOptions options, string control)
    {
        using var clock = new ManualClock(); using var socket = new SocketFixture();
        socket.BeforeDelivery = () => clock.Advance(50);
        await using var connection = new GptLiveConnection(socket);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var decisions = new WandDecisionService((_, _) => throw new Exception("Express-only control reached paid selection"));
        var consumed = Channel.CreateUnbounded<string>();
        var run = new AutonomousLiveDialogue(connection, decisions, clock).RunAsync(Call(options, lifetime),
            (_, _) => Task.CompletedTask, (text, _) => { consumed.Writer.TryWrite(text); return Task.CompletedTask; },
            _ => Task.CompletedTask, (_, _) => throw new Exception("Express-only control reached checkpoint"),
            () => (63, 7, true), _ => Task.CompletedTask, lifetime.Token);
        await socket.Command("session.instructions.append", lifetime.Token);
        socket.Transcript(false, "Co chceš?", 0, 1000);
        socket.Transcript(true, control, 1100, 2000);
        await consumed.Reader.ReadAsync(lifetime.Token); socket.Delegate("empty-express", 2000); clock.Advance(1300);
        var commentary = socket.Command("session.commentary.append", lifetime.Token);
        await AdvanceUntilAsync(commentary, run, clock, lifetime.Token);
        var context = await commentary;
        Check(context.Contains("Chybí odpověď hosta") && !run.IsCompleted,
            "express changes tempo but does not provide selection evidence or physical proposal");
        lifetime.Cancel();
        try { await run; } catch (OperationCanceledException) { }
    }

    private static async Task VerifyExpressAfterNeedsInformation(GptLiveOptions options)
    {
        using var clock = new ManualClock(); using var socket = new SocketFixture();
        socket.BeforeDelivery = () => clock.Advance(50);
        await using var connection = new GptLiveConnection(socket);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var calls = 0;
        using var decisions = new WandDecisionService((input, _) =>
        {
            calls++;
            Check(input.Evidence.Count == 1 && input.Evidence[0].Text == "Chci přesnost.", "express retains only substantive evidence");
            if (calls == 1)
            {
                Check(!input.DirectSelection, "first delegation uses standard policy");
                return Task.FromResult(new WandDecisionResult(WandDecisionStatus.NeedsInformation, null, [], "Chybí další preference.", input.Revision, input.AvailabilityRevision));
            }
            Check(calls == 2 && input.DirectSelection, "express replaces obsolete needs-information policy once");
            return Task.FromResult(Proposal(input, 1));
        });
        var consumed = Channel.CreateUnbounded<string>();
        var run = new AutonomousLiveDialogue(connection, decisions, clock).RunAsync(Call(options, lifetime),
            (_, _) => Task.CompletedTask, (text, _) => { consumed.Writer.TryWrite(text); return Task.CompletedTask; },
            _ => Task.CompletedTask, (_, _) => throw new Exception("No physical checkpoint expected"),
            () => (63, 7, true), _ => Task.CompletedTask, lifetime.Token);
        await socket.Command("session.instructions.append", lifetime.Token);
        socket.Transcript(false, "Co chceš?", 0, 1000);
        socket.Transcript(true, "Chci přesnost.", 1100, 2000);
        await consumed.Reader.ReadAsync(lifetime.Token); socket.Delegate("standard-first", 2000);
        var first = socket.Command("session.commentary.append", lifetime.Token);
        await AdvanceUntilAsync(first, run, clock, lifetime.Token);
        await first;
        socket.Transcript(false, "Co dalšího?", 3000, 4000);
        socket.Transcript(true, "Vyber rovnou.", 4100, 5000);
        await consumed.Reader.ReadAsync(lifetime.Token); socket.Delegate("express-second", 5000);
        var second = socket.Command("session.commentary.append", lifetime.Token);
        await AdvanceUntilAsync(second, run, clock, lifetime.Token);
        await second;
        Check(calls == 2, "express with retained evidence does not reuse the previous needs-information cache");
        lifetime.Cancel();
        try { await run; } catch (OperationCanceledException) { }
    }

    private static async Task VerifyCharacterEvidenceWithdrawal(GptLiveOptions options, string control, bool sameReference = false)
    {
        using var clock = new ManualClock(); using var socket = new SocketFixture();
        socket.BeforeDelivery = () => clock.Advance(50);
        await using var connection = new GptLiveConnection(socket);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var calls = 0;
        using var decisions = new WandDecisionService((input, _) =>
        {
            calls++;
            if (calls == 2)
            {
                Check(input.Evidence.Count == 1 && input.Evidence[0].Text.Contains("přesnost")
                    && !input.Evidence[0].Text.Contains("klíče") && !input.Evidence[0].Text.Contains(control),
                    "new decision uses replacement, not withdrawn fact or control");
            }
            Check(calls <= 2, "withdrawal does not create additional decisions");
            return Task.FromResult(Proposal(input, calls));
        });
        var consumed = Channel.CreateUnbounded<string>();
        var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new AutonomousLiveDialogue(connection, decisions, clock).RunAsync(Call(options, lifetime),
            (_, _) => Task.CompletedTask, (text, _) => { consumed.Writer.TryWrite(text); return Task.CompletedTask; },
            _ => Task.CompletedTask, (_, _) => throw new Exception("Withdrawn candidate reached playback checkpoint"),
            () => (63, 7, true), _ => Task.CompletedTask, lifetime.Token,
            terminalState: state => { if (state == "input_changed") { invalidated.TrySetResult(); } });
        await socket.Command("session.instructions.append", lifetime.Token);
        socket.Transcript(false, "Co chceš kouzlem spravit?", 0, 1000);
        socket.Transcript(true, "Ztrácím klíče denně. Prostě mi nějakou vyber.", 1100, 2000);
        await consumed.Reader.ReadAsync(lifetime.Token); socket.Delegate("before-withdrawal", 2000);
        var initialResult = socket.Command("session.commentary.append", lifetime.Token);
        await AdvanceUntilAsync(initialResult, run, clock, lifetime.Token);
        await initialResult;
        socket.Transcript(false, "Výběr mám.", 3000, 4000);
        socket.Transcript(true, control, 4100, 4500);
        await consumed.Reader.ReadAsync(lifetime.Token);
        await AdvanceUntilAsync(invalidated.Task, run, clock, lifetime.Token);
        socket.Delegate("pure-withdrawal", 4500); clock.Advance(1300);
        if (!sameReference) { socket.Transcript(false, "Co opravdu chceš?", 5000, 6000); }
        socket.Transcript(true, " Oprava, chci přesnost. Vyber rovnou.", 6100, 7000);
        await consumed.Reader.ReadAsync(lifetime.Token);
        socket.Delegate("replacement", 7000); clock.Advance(1300);
        var updated = socket.Command("session.commentary.append", lifetime.Token);
        await AdvanceUntilAsync(updated, run, clock, lifetime.Token);
        await updated;
        Check(calls == 2 && !run.IsCompleted, "withdrawn terminal cannot publish; valid replacement can be recomputed");
        lifetime.Cancel();
        try { await run; } catch (OperationCanceledException) { }
    }

    private static async Task VerifyCharacterFlow(GptLiveOptions options, bool interrupted = false, bool express = false)
    {
        using var clock = new ManualClock(); using var socket = new SocketFixture();
        socket.BeforeDelivery = () => clock.Advance(50);
        await using var connection = new GptLiveConnection(socket);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var decisionCalls = 0;
        using var decisions = new WandDecisionService((input, _) =>
        {
            decisionCalls++;
            Check(input.CostumeContext.Length == 0 && input.MemoryContext.Length == 0, "roast and historical/image material do not alter scoring");
            return Task.FromResult(Proposal(input, 1));
        });
        var consumed = Channel.CreateUnbounded<string>();
        var chunks = Channel.CreateUnbounded<int>();
        var checkpoints = Channel.CreateUnbounded<LivePlaybackRequest>();
        var drained = new TaskCompletionSource<LivePlaybackConfirmation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var played = new List<string>();
        var cancellations = Channel.CreateUnbounded<long>();
        var afterCancellation = false;
        var freshDirective = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new AutonomousLiveDialogue(connection, decisions, clock).RunAsync(Call(options, lifetime),
            (pcm, _) => { chunks.Writer.TryWrite(pcm.Length); return Task.CompletedTask; },
            (text, _) => { consumed.Writer.TryWrite(text); return Task.CompletedTask; }, _ => Task.CompletedTask,
            (checkpoint, _) => { checkpoints.Writer.TryWrite(checkpoint); return drained.Task; },
            () => (63, 7, true), _ => Task.CompletedTask, lifetime.Token, stylePlayed: (style, _) => played.Add(style),
            cancelAudio: (generation, _) => { afterCancellation = true; cancellations.Writer.TryWrite(generation); return Task.CompletedTask; },
            terminalState: state => { if (afterCancellation && state == "character_directive_accepted") { freshDirective.TrySetResult(); } });
        await socket.Command("session.instructions.append", lifetime.Token);
        socket.Transcript(false, "Co bys spravil kouzlem?", 0, 1000);
        if (express)
        {
            socket.Transcript(true, "Chci přesnost.", 1100, 1400);
            await consumed.Reader.ReadAsync(lifetime.Token);
            socket.Transcript(false, "Dobře, krátce.", 1500, 1600);
        }
        socket.Transcript(true, express ? "Vyber rovnou." : "Prostě mi nějakou vyber.", express ? 1700 : 1100, 2000);
        await consumed.Reader.ReadAsync(lifetime.Token);
        socket.Delegate("character-selection", 2000);
        socket.Emit(new { type = "session.usage.updated", usage = new { seconds = 1 } });
        clock.Advance(1300);
        var commentary = socket.Command("session.commentary.append", lifetime.Token);
        await AdvanceUntilAsync(commentary, run, clock, lifetime.Token);
        var result = await commentary;
        Check(!result.Contains("Poslední slovo") && !result.Contains("compartment"), "identity is not delivered to limited actor");
        if (interrupted)
        {
            socket.Transcript(false, "Poslední slovo —", 3000, 4000);
            socket.Transcript(true, "Mírněji.", 4100, 4200);
            await cancellations.Reader.ReadAsync(lifetime.Token);
            await consumed.Reader.ReadAsync(lifetime.Token);
            socket.Audio(); // old stream packets are not a new response
            socket.Emit(new { type = "session.usage.updated", usage = new { seconds = 2 } });
            clock.Advance(2000);
            await AdvanceUntilAsync(freshDirective.Task, run, clock, lifetime.Token);
            socket.Emit(new { type = "session.usage.updated", usage = new { seconds = 3 } });
            clock.Advance(1000);
            socket.Audio(); // legal new audio before its transcript
        }
        socket.Transcript(false, "Výběr mám. Mechanika není potvrzená. Zasraná technika.", interrupted ? 5000 : 3000, interrupted ? 6000 : 4000);
        socket.Audio(); await chunks.Reader.ReadAsync(lifetime.Token);
        if (interrupted) { await chunks.Reader.ReadAsync(lifetime.Token); }
        clock.Advance(2000);
        var checkpointRead = checkpoints.Reader.ReadAsync(lifetime.Token).AsTask();
        await AdvanceUntilAsync(checkpointRead, run, clock, lifetime.Token);
        var checkpoint = await checkpointRead;
        drained.SetResult(new(checkpoint.PlaybackId, checkpoint.PcmBytes, true));
        var outcome = await run.WaitAsync(lifetime.Token);
        Check(outcome.PlaybackVerified && outcome.Decision?.Compartment == 1 && played.Count == 2 && decisionCalls == 1
            && !outcome.Reply.Reply.Contains("Poslední slovo"), "one decision, response recovery and no cancelled transcript contamination");
    }

    private static async Task AdvanceUntilAsync(Task expected, Task run, ManualClock clock, CancellationToken token)
    {
        for (var step = 0; step < 300 && !expected.IsCompleted && !run.IsCompleted; step++)
        {
            clock.Advance(50);
            await Task.Delay(1, token); // Scheduler handoff; durations remain on the fake clock.
        }
        if (!expected.IsCompleted && run.IsCompleted) { await run; }
        await expected.WaitAsync(token);
    }

    private static async Task VerifyCharacterCancellation(GptLiveOptions options)
    {
        using var clock = new ManualClock(); using var socket = new SocketFixture();
        socket.BeforeDelivery = () => clock.Advance(50);
        await using var connection = new GptLiveConnection(socket);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var decisions = new WandDecisionService((_, _) => throw new Exception("Control requested a decision"));
        var consumed = Channel.CreateUnbounded<string>(); var chunks = Channel.CreateUnbounded<int>();
        var cancelled = Channel.CreateUnbounded<long>();
        var suppressed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new AutonomousLiveDialogue(connection, decisions, clock).RunAsync(Call(options, lifetime),
            (pcm, _) => { chunks.Writer.TryWrite(pcm.Length); return Task.CompletedTask; },
            (text, _) => { consumed.Writer.TryWrite(text); return Task.CompletedTask; }, _ => Task.CompletedTask,
            (_, _) => throw new Exception("Cancelled output reached checkpoint"), () => (63, 7, true),
            _ => Task.CompletedTask, lifetime.Token,
            cancelAudio: (generation, _) => { cancelled.Writer.TryWrite(generation); return Task.CompletedTask; },
            terminalState: state => { if (state == "character_audio_suppressed") { suppressed.TrySetResult(); } });
        await socket.Command("session.instructions.append", lifetime.Token);
        socket.Transcript(false, "Tak co tvoje ego?", 0, 1000); socket.Audio();
        await chunks.Reader.ReadAsync(lifetime.Token);
        socket.Transcript(true, "Bez nadávek.", 1100, 2000);
        await cancelled.Reader.ReadAsync(lifetime.Token); await consumed.Reader.ReadAsync(lifetime.Token);
        socket.Audio(); socket.Emit(new { type = "session.usage.updated", usage = new { seconds = 2 } });
        await suppressed.Task.WaitAsync(lifetime.Token);
        clock.Advance(1500);
        socket.Transcript(false, "Dobře. Zůstanu u hůlek.", 2000, 2050);
        socket.Transcript(true, "Nechci pokračovat.", 2100, 2500);
        try { await run.WaitAsync(lifetime.Token); throw new Exception("Opt-out ignored"); }
        catch (OperationCanceledException) { }
        Check(!chunks.Reader.TryRead(out _) && !socket.Sent.Any(text => text.Contains("session.commentary.append")),
            "late cancelled audio and end participation cannot publish a proposal");
    }

    private static async Task VerifySplitVoiceControl(GptLiveOptions options)
    {
        using var clock = new ManualClock(); using var socket = new SocketFixture();
        socket.BeforeDelivery = () => clock.Advance(50);
        await using var connection = new GptLiveConnection(socket);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var decisions = new WandDecisionService((_, _) => throw new Exception("Split word requested decision"));
        var consumed = Channel.CreateUnbounded<string>();
        var run = new AutonomousLiveDialogue(connection, decisions, clock).RunAsync(Call(options, lifetime),
            (_, _) => Task.CompletedTask, (text, _) => { consumed.Writer.TryWrite(text); return Task.CompletedTask; },
            _ => Task.CompletedTask, (_, _) => throw new Exception("No checkpoint"), () => (63, 7, true),
            _ => Task.CompletedTask, lifetime.Token);
        await socket.Command("session.instructions.append", lifetime.Token);
        socket.Transcript(false, "Co bys spravil?", 0, 1000);
        await connection.SendAudioAsync(new byte[] { 0, 32, 0, 32 }, lifetime.Token);
        socket.Transcript(true, "Stop", 1100, 1200); await consumed.Reader.ReadAsync(lifetime.Token);
        clock.Advance(1000);
        Check(!run.IsCompleted, "unfinished Stop prefix is not an accepted end while PCM is active");
        socket.Transcript(true, "ařské kouzlo je moje oblíbené.", 1200, 2000);
        await consumed.Reader.ReadAsync(lifetime.Token); clock.Advance(1300);
        socket.Emit(new { type = "session.usage.updated", usage = new { seconds = 1 } });
        var update = socket.Command("session.instructions.append", lifetime.Token);
        if (await Task.WhenAny(update, run) == run) { await run; }
        await update;
        Check(!run.IsCompleted, "stable Stopařské fantasy does not terminate participation");
        socket.Transcript(false, "Dobře, pokračuj.", 2100, 2200);
        socket.Transcript(true, "Stop.", 2300, 2400);
        socket.Emit(new { type = "session.usage.updated", usage = new { seconds = 2 } });
        clock.Advance(500);
        try { await run.WaitAsync(lifetime.Token); throw new Exception("Complete voice Stop ignored"); }
        catch (OperationCanceledException) { }
    }

    private static async Task VerifyPcmCancellationFence(GptLiveOptions options)
    {
        using var clock = new ManualClock(); using var socket = new SocketFixture();
        socket.BeforeDelivery = () => clock.Advance(50);
        await using var connection = new GptLiveConnection(socket);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var decisions = new WandDecisionService((_, _) => throw new Exception("No delegation"));
        var chunks = Channel.CreateUnbounded<int>(); var cancellations = Channel.CreateUnbounded<long>();
        var suppressed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new AutonomousLiveDialogue(connection, decisions, clock).RunAsync(Call(options, lifetime),
            (pcm, _) => { chunks.Writer.TryWrite(pcm.Length); return Task.CompletedTask; }, (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask, (_, _) => throw new Exception("No checkpoint"), () => (63, 7, true),
            _ => Task.CompletedTask, lifetime.Token,
            cancelAudio: (generation, _) => { cancellations.Writer.TryWrite(generation); return Task.CompletedTask; },
            terminalState: state => { if (state == "character_audio_suppressed") { suppressed.TrySetResult(); } });
        await socket.Command("session.instructions.append", lifetime.Token);
        socket.Transcript(false, "Co bys spravil?", 0, 1000); socket.Audio();
        await chunks.Reader.ReadAsync(lifetime.Token);
        await connection.SendAudioAsync(new byte[] { 0, 32, 0, 32 }, lifetime.Token);
        clock.Advance(50); await cancellations.Reader.ReadAsync(lifetime.Token);
        clock.Advance(2000);
        // No new transcript/directive exists. Old timeline+old ACK may not
        // authorize recovery merely because PCM subsequently became quiet.
        socket.Transcript(false, "Staré pokračování.", 1100, 2000); socket.Audio();
        await suppressed.Task.WaitAsync(lifetime.Token);
        Check(!chunks.Reader.TryRead(out _), "PCM-only barge-in retires previous directive ACK");
        lifetime.Cancel();
        try { await run; } catch (OperationCanceledException) { }
    }

    internal static async Task RunAsync()
    {
        var defaults = GptLiveOptions.Read(new ConfigurationBuilder().Build());
        Check(defaults.Mode == LiveActorMode.LegacyBackendScripted, "safe legacy default");
        var options = defaults with { Mode = LiveActorMode.AutonomousActor, DelegationContextWaitMs = 0, OutputPauseMs = 500, TerminalQuietMs = 1000 };
        var olli = LivePromptSnapshot.Create(PersonalityCatalog.OllivanderId, options);
        var party = LivePromptSnapshot.Create(PersonalityCatalog.DefaultId, options);
        Check(olli.Policy == new LiveConversationPolicy(2, 3, true) && party.Policy == new LiveConversationPolicy(4, 7, false), "per-personality policy");
        Check(olli.Instructions.Contains("Nejsi Ollivander osobně") && !party.Instructions.Contains("Ollivanderův prastarý")
            && !party.Instructions.Contains("dvě zodpovězené"), "dedicated adaptation never overwrites party identity");
        Check(!olli.Instructions.Contains("Vyslov dodanou") && !party.Instructions.Contains("server nedodá repliku"), "autonomous prompt has no mandatory scripted-response gate");
        Check(olli.Sha256 != PersonalityCatalog.Inventory.Single(p => p.Id == PersonalityCatalog.OllivanderId).PromptSha256, "source hash and derived prompt hash differ");
        var overrideOptions = options with { UsualAnswersOverride = 3, MaximumAnswersOverride = 4 };
        Check(LivePromptSnapshot.Create(PersonalityCatalog.OllivanderId, overrideOptions).Instructions.Contains("otázek: 3; nejvýše 4")
            && overrideOptions.Policy(PersonalityCatalog.OllivanderId).MaximumAnswers == 4, "one documented admin-policy priority");
        VerifyProjection(options);
        VerifyTimeline(options);
        VerifySpeechGuards(options);
        VerifyDecisionModels();
        using var noModel = new WandDecisionService((_, _) => throw new Exception("Unnecessary model call"));
        var input = new WandDecisionInput(2, 0, 7, [new("a", "nevím")], olli.Policy, 2, false);
        Check((await noModel.DecideAsync(input, CancellationToken.None)).Status == WandDecisionStatus.NoAvailableWand, "empty ReadyMask is deterministic");
        Check((await noModel.DecideAsync(input with { ReadyMask = 63, Policy = party.Policy, AnswerCandidates = 2 }, CancellationToken.None)).Status
            == WandDecisionStatus.NeedsInformation, "party keeps its own policy without an early paid decision or Ollivander cadence");
        var request = WandDecisionService.BuildRequest(input with { ReadyMask = 63 });
        Check(!request.Contains("\"reply\"") && request.Contains("decide_wand") && request.Contains("gpt-6.1-sol"), "structured facts use existing model without a monologue contract");
        var escaped = WandDecisionService.BuildRequest(input with { ReadyMask = 63, Evidence = Enumerable.Range(0, 8)
            .Select(i => new DecisionEvidence("answer-" + i, string.Concat(Enumerable.Repeat("\\\"\nžluťoučký", 500)))).ToArray() });
        Check(System.Text.Encoding.UTF8.GetByteCount(escaped) <= ResponsesTextDialogBudget.MaxRequestBytes,
            "long Czech/escaped evidence fits actual serialized request budget, retaining every answer reference");
        Check(Enumerable.Range(0, 8).All(i => escaped.Contains("answer-" + i)), "budgeting does not discard old answers as a two-exchange shortcut");
        using var invalid = new WandDecisionService((i, _) => Task.FromResult(new WandDecisionResult(WandDecisionStatus.ValidProposal, 6,
            [new("a", "made up", "fact")], "", i.Revision, i.AvailabilityRevision)));
        try { await invalid.DecideAsync(input with { ReadyMask = 1 }, CancellationToken.None); throw new Exception("Invalid proposal accepted"); }
        catch (InvalidDataException) { }
        using var tooManyQuestions = new WandDecisionService((i, _) => Task.FromResult(new WandDecisionResult(WandDecisionStatus.NeedsInformation,
            null, [], "Ještě otázka", i.Revision, i.AvailabilityRevision)));
        foreach (var i in new[] { input with { ReadyMask = 1, DirectSelection = true }, input with { ReadyMask = 1, AnswerCandidates = 3 } })
        {
            try { await tooManyQuestions.DecideAsync(i, CancellationToken.None); throw new Exception("Endless diagnostic question"); }
            catch (InvalidDataException) { }
        }
        await VerifyConversation(options, correction: false, fractionalTimeline: true);
        await VerifyConversation(options, correction: true);
        await VerifyConversation(options, correction: false, interrupt: true);
        await VerifyConversation(options with { DelegationTimeoutMs = 2000, DelegationContextWaitMs = 350 }, correction: false,
            naturalVerdict: true, visitorStillSpeaking: true, acknowledge: true, resumeDuringDecision: true);
        await VerifyConversation(options, correction: false, naturalVerdict: true, repairVerdict: true);
        await VerifyMissingContext(options);
        await VerifyDecisionFailure(options);
        await VerifyProviderFailure(options);
        await VerifyPlaybackRegistry();
        Console.WriteLine("Autonomous Live: frozen prompts/policy, numeric timelines, timed long Czech input, delegation ordering/duplicates, stale correction, playback, failure diagnostics and native wire passed.");
    }

    private static void VerifyDecisionModels()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:GptLive:DecisionModel"] = WandDecisionBudget.Luna
        }).Build();
        using var service = new WandDecisionService(configuration);
        Check(service.Model == WandDecisionBudget.Luna && GptLiveOptions.Read(configuration).DecisionModel == service.Model,
            "Luna is isolated to the Live factual decision");
        Check(!ResponsesTextDialogBudget.IsSupportedModel(service.Model), "Luna does not re-enable the legacy comedian");
        var input = new WandDecisionInput(2, 16, 7, [new("a", "Improvizuji každý den.")], new(2, 3, true), 2, false);
        using var request = JsonDocument.Parse(WandDecisionService.BuildRequest(input, service.Model));
        Check(request.RootElement.GetProperty("model").GetString() == service.Model
            && request.RootElement.GetProperty("reasoning").GetProperty("effort").GetString() == "none"
            && request.RootElement.GetProperty("max_output_tokens").GetInt32() == 512,
            "focused non-reasoning Luna request avoids the legacy monologue");
        var candidates = request.RootElement.GetProperty("input").GetString()!;
        Check(candidates.Contains("Kaštanová hůlka") && !candidates.Contains("Jasanová hůlka"), "sparse physical candidate mask is preserved");
        Check(WandDecisionBudget.Price(service.Model, "default", 14000, 512, service.Model) == 2006
            && WandDecisionBudget.Price(service.Model, "default", 1, 0, service.Model) == 1
            && service.Reservation(input) > 2006, "cache-write rates, micro-dollar rounding and conservative reservation");
        Check(WandDecisionBudget.Price(ResponsesTextDialogBudget.Model, "default", 14000, 1200, ResponsesTextDialogBudget.Model)
            == ResponsesTextDialogBudget.CalculateUsageMicroUsd(ResponsesTextDialogBudget.Model, "default", 14000, 1200),
            "Sol pricing compatibility");
        var reasons = new[] { new DecisionReason("a", "Improvizuji", "Host popsal improvizaci.") };
        using var response = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            status = "completed", model = service.Model, service_tier = "default",
            output = new[] { new { type = "function_call", name = "decide_wand", arguments = JsonSerializer.Serialize(new
            { status = "valid_proposal", compartment = 5, reasons, missingInformation = "" }, RealtimeProtocol.JsonOptions) } },
            usage = new { input_tokens = 14000, output_tokens = 512 }
        }));
        var result = WandDecisionService.Parse(response.RootElement, input, service.Model);
        Check(result.Compartment == 5 && result.CostMicroUsd == 2006 && result.AvailabilityRevision == 7,
            "Luna function result remains grounded and correlated");
        foreach (var invalid in new[] { input with { ReadyMask = 1 }, input with { ExcludeCompartment = 5 },
            input with { Evidence = [new("a", "Plánuji vše.")] },
            input with { Policy = new(4, 7, false), AnswerCandidates = 2 } })
        {
            try { WandDecisionService.Parse(response.RootElement, invalid, service.Model); throw new Exception("Unsafe Luna decision accepted"); }
            catch (InvalidDataException) { }
        }
        foreach (var (model, tier, count) in new[] { ("gpt-6.1-sol", "default", 1), (service.Model, "priority", 1), (service.Model, "default", -1) })
        {
            try { WandDecisionBudget.Price(model, tier, count, 1, service.Model); throw new Exception("Unknown usage accepted"); }
            catch (TextDialogUnavailableException) { }
        }
        try { WandDecisionService.Parse(response.RootElement, input); throw new Exception("Unexpected model accepted"); }
        catch (TextDialogUnavailableException) { }
        foreach (var model in new[] { "gpt-6-astra", "arbitrary-model" })
        {
            var invalid = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Backend:GptLive:DecisionModel"] = model }).Build();
            try { GptLiveOptions.Read(invalid); throw new Exception("Unsupported decision model accepted"); }
            catch (InvalidOperationException) { }
        }
    }

    private static void VerifyProjection(GptLiveOptions options)
    {
        var p = new LiveConversationProjection(options);
        Append(p, false, "Co tě nejvíc štve?", 0, 500);
        Append(p, true, "Cože, neslyšel jsem.", 600, 1000);
        Check(p.AnswerCandidates == 0, "request to repeat is not a diagnostic answer");
        Append(p, false, "Říkal jsem: co tě nejvíc štve?", 1100, 1400);
        Append(p, true, "Nev", 1500, 1600); Append(p, true, "ím.", 1600, 1800);
        Check(p.AnswerCandidates == 1 && p.LatestInput == "Nevím.", "word-split undecided answer is usable once");
        Append(p, false, "Jak řešíš chaos?", 1900, 2200);
        Append(p, true, new string('ž', 9000), 2300, 10000);
        Append(p, true, " pořád plánuji.", 10001, 15000);
        Check(p.AnswerCandidates == 2 && p.Segments.Sum(s => s.Text.Length) > 1000 && p.LatestInput.EndsWith("plánuji."), "long story is bounded without ending session");
        Append(p, true, " Oprava, improvizuji.", 15000, 16000);
        Check(p.HasCorrection && p.Evidence().Count == 1 && p.Evidence()[0].Text.Contains("improvizuji"), "latest semantic correction supersedes conflicting candidate evidence");
        Append(p, true, " každý den.", 16000, 17000);
        Check(!p.HasCorrection && p.AnswerCandidates == 2, "continuation does not repeatedly invalidate/count questions");
        Append(p, true, " Prostě mi nějakou vyber.", 17000, 18000);
        Check(p.DirectSelection, "direct choice request stops diagnostic policy");
        Check(LiveConversationProjection.Classify("Přestaň mluvit") == LiveRequestIntent.StopSpeech
            && LiveConversationProjection.Classify("Zruš výběr") == LiveRequestIntent.CancelConversation
            && LiveConversationProjection.Classify("Chci jinou hůlku") == LiveRequestIntent.Alternative, "speech stop, cancellation and alternative are distinct");
        var text = string.Concat(Enumerable.Repeat("Příliš žluťoučký kůň 🧙. ", 1000));
        var bounded = LiveTextBudget.Bound(text);
        Check(LiveTextBudget.UpperBound(bounded) <= 500 && !bounded.Contains('\uFFFD'), "Czech and surrogate pairs respect conservative 500-token append bound");
    }

    private static void VerifyTimeline(GptLiveOptions options)
    {
        var projection = new LiveConversationProjection(options);
        using (var question = JsonDocument.Parse("""{"delta":"Jak řešíš chaos?","start_ms":0.0,"end_ms":1000.25}"""))
            projection.Append(question.RootElement, false);
        using (var first = JsonDocument.Parse("""{"delta":"Nev","start_ms":1100.0,"end_ms":1200.25}"""))
            projection.Append(first.RootElement, true);
        using (var second = JsonDocument.Parse("""{"delta":"ím.","start_ms":1200.25,"end_ms":1400.5}"""))
            projection.Append(second.RootElement, true);
        Check(projection.LatestInput == "Nevím." && projection.AnswerCandidates == 1
            && projection.Segments[1].StartMs == 1100 && projection.Segments[1].EndMs == 1200.25
            && projection.Segments[2].EndMs == 1400.5, "decimal numeric notation retains timing and split-word answer semantics");
        foreach (var time in new[] { "-0.1", "200000.1", "1e400", "\"1100\"", "null" })
        {
            using var invalid = JsonDocument.Parse("{\"delta\":\"text\",\"start_ms\":" + time + ",\"end_ms\":200000}");
            try { projection.Append(invalid.RootElement, true); throw new Exception("Invalid timeline accepted"); }
            catch (InvalidDataException error) { Check(LiveFailureDiagnostics.Code(error) == "live_event_timing_invalid", "invalid timestamp has a safe diagnostic"); }
        }
        foreach (var invalid in new[] { """{"delta":"text","end_ms":1000}""", """{"delta":"text","start_ms":1000.5,"end_ms":1000.25}""" })
        {
            using var document = JsonDocument.Parse(invalid);
            try { projection.Append(document.RootElement, true); throw new Exception("Missing/inverted timeline accepted"); }
            catch (InvalidDataException) { }
        }
        Check(projection.Segments.Count == 3 && projection.LatestInput == "Nevím.", "rejected metadata cannot mutate conversation evidence");
    }

    private static void VerifySpeechGuards(GptLiveOptions options)
    {
        using var clock = new ManualClock();
        var activity = new LiveInputActivity(clock, options.SpeechAmplitudeThreshold);
        var click = new byte[4096]; click[1] = 32;
        activity.Observe(click);
        Check(activity.QuietFor(options.InputQuietMs), "a single PCM click is not a speech turn");
        activity.Observe(new byte[] { 0, 32, 0, 32 });
        clock.Advance(1100); activity.Observe(new byte[4096]);
        Check(!activity.QuietFor(options.InputQuietMs), "brief breathing pause remains part of the guest turn");
        clock.Advance(100);
        Check(activity.QuietFor(options.InputQuietMs), "sustained input quiet eventually releases the turn");
        var catalog = PartyWandCatalog.Available(63);
        foreach (var (compartment, spoken) in new[]
        {
            (1, "Pro tebe vybírám Poslední slovo"),
            (2, "Vybírám třešňovou hůlku Parketový kompas"),
            (3, "Počítej s Výborem pro špatné nápady"),
            (4, "Budeš se Sametovým alibi"),
            (5, "S Kocovinou věštců to zvládneš"),
            (6, "S Knihovní bouří to půjde")
        })
            Check(LiveTerminalSpeech.Ready(spoken, compartment, catalog), "Czech catalog title identifies the selected item without punctuation");
        Check(!LiveTerminalSpeech.Ready("Poslední slovo —", 1, catalog)
            && !LiveTerminalSpeech.Ready("Poslední slovo...", 1, catalog), "explicitly unfinished verdict keeps listening");
        Check(!LiveTerminalSpeech.Ready("Knihovní bouře.", 1, catalog)
            && !LiveTerminalSpeech.Ready("Poslední slovník.", 1, catalog)
            && !LiveTerminalSpeech.Ready("Poslední slovo a Knihovní bouře.", 1, catalog), "wrong, invented and ambiguous catalog titles cannot publish a selection");
        Check(LiveConversationProjection.Classify("Dobře, ukaž ji.") == LiveRequestIntent.Acknowledge
            && LiveConversationProjection.Classify("Díky moc!") == LiveRequestIntent.Thanks
            && LiveConversationProjection.Classify("Díky, ale chci jinou hůlku.") == LiveRequestIntent.Alternative
            && LiveConversationProjection.Classify("Dobře, ale raději všechno plánuji.") == LiveRequestIntent.Selection,
            "acknowledgment is distinct from a correction or alternative");
    }

    private static async Task VerifyConversation(GptLiveOptions options, bool correction, bool interrupt = false,
        bool naturalVerdict = false, bool visitorStillSpeaking = false, bool acknowledge = false,
        bool resumeDuringDecision = false, bool repairVerdict = false, bool fractionalTimeline = false)
    {
        using var clock = new ManualClock();
        using var socket = new SocketFixture();
        socket.BeforeDelivery = () => clock.Advance(50);
        await using var connection = new GptLiveConnection(socket);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var correctionSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource<WandDecisionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        WandDecisionInput? capturedInput = null;
        using var decisions = new WandDecisionService((i, _) =>
        {
            calls++; entered.TrySetResult();
            if ((correction || resumeDuringDecision) && calls == 1) { capturedInput = i; return first.Task; }
            var evidence = i.Evidence.Last();
            Check(!correction || evidence.Text.Contains("improvizuji"), "corrected input reaches decision service");
            return Task.FromResult(Proposal(i, correction ? 2 : 1));
        });
        var call = Call(options, lifetime);
        var playerRequests = Channel.CreateUnbounded<LivePlaybackRequest>();
        var playerDone = new TaskCompletionSource<LivePlaybackConfirmation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumed = Channel.CreateUnbounded<string>();
        var audioConsumed = Channel.CreateUnbounded<int>();
        var states = Channel.CreateUnbounded<string>();
        var pcmBytes = 0;
        var run = new AutonomousLiveDialogue(connection, decisions, clock).RunAsync(call,
            (bytes, _) => { pcmBytes += bytes.Length; audioConsumed.Writer.TryWrite(bytes.Length); return Task.CompletedTask; },
            (text, _) => { consumed.Writer.TryWrite(text); if (text.Contains("improvizuji")) correctionSeen.TrySetResult(); return Task.CompletedTask; },
            _ => Task.CompletedTask,
            (r, _) => { playerRequests.Writer.TryWrite(r); return playerDone.Task; },
            () => (63, 7, true), _ => Task.CompletedTask, lifetime.Token,
            terminalState: state => states.Writer.TryWrite(state));
        await socket.Command("session.instructions.append", lifetime.Token);
        socket.Transcript(false, "Co děláš, když se plán rozpadne?", 0, 1000);
        socket.Transcript(true, "Plánuji všechno.", fractionalTimeline ? 1100.25 : 1100, fractionalTimeline ? 2000.5 : 2000);
        await consumed.Reader.ReadAsync(lifetime.Token);
        Check(calls == 0, "ordinary greeting/question/reaction needs no backend model");
        if (visitorStillSpeaking) await connection.SendAudioAsync(new byte[] { 0, 32, 0, 32 }, lifetime.Token);
        if (!correction)
        {
            socket.Transcript(false, "Jak zvládáš chaos?", 2100, 2300);
            socket.Delegate("d-one", fractionalTimeline ? 2500.75 : 2500); // newer answer is not transcribed yet
            socket.Transcript(true, "Nevím.", 2400, 2600);
            await consumed.Reader.ReadAsync(lifetime.Token);
        }
        socket.Delegate("d-one", fractionalTimeline ? 2500.75 : 2500); socket.Delegate("d-one", 2500); // normal duplicate
        clock.Advance(50);
        if (visitorStillSpeaking)
        {
            while (await states.Reader.ReadAsync(lifetime.Token) != "input_active") { }
            Check(calls == 0, "a transcript gap cannot dispatch work over active guest audio");
            for (var part = 0; part < 6; part++)
            {
                clock.Advance(500);
                await connection.SendAudioAsync(new byte[] { 0, 32, 0, 32 }, lifetime.Token);
                socket.Transcript(true, " Ještě pokračuji.", 2700 + part * 40, 2730 + part * 40);
                await consumed.Reader.ReadAsync(lifetime.Token);
                Check(calls == 0, "a long guest answer is not mistaken for missing delegation context");
            }
            clock.Advance(1300);
        }
        var enteredOrFailed = await Task.WhenAny(entered.Task, run).WaitAsync(lifetime.Token);
        if (enteredOrFailed == run) await run;
        await entered.Task.WaitAsync(lifetime.Token);
        if (resumeDuringDecision)
        {
            await connection.SendAudioAsync(new byte[] { 0, 32, 0, 32 }, lifetime.Token);
            first.SetResult(Proposal(capturedInput!, 1));
            while (await states.Reader.ReadAsync(lifetime.Token) != "result_input_active") { }
            Check(!socket.Sent.Any(s => s.Contains("session.commentary.append")), "a ready decision cannot interrupt resumed guest speech before a transcript arrives");
            clock.Advance(1300);
        }
        if (correction)
        {
            socket.Transcript(true, " Oprava, improvizuji.", 2100, 2500);
            await correctionSeen.Task.WaitAsync(lifetime.Token);
            first.SetResult(Proposal(new(1, 63, 7, [new("answer-0", "Plánuji všechno.")], options.Policy(PersonalityCatalog.OllivanderId), 1, false), 6));
        }
        var result = await socket.Command("session.commentary.append", lifetime.Token);
        Check(calls == (correction ? 2 : 1) && !result.Contains("ZASTARALÝ"), "duplicate/late stale work is not published or replayed");
        var name = PartyWandCatalog.Available(63).Single(w => w.Compartment == (correction ? 2 : 1)).DisplayName;
        if (options.Character.Enabled) { name = "Výběr mám"; }
        if (naturalVerdict) name = "Poslední slovo";
        socket.Transcript(false, name + " —", 3000, 4000);
        socket.Audio();
        await audioConsumed.Reader.ReadAsync(lifetime.Token);
        clock.Advance(6000);
        Check(!playerRequests.Reader.TryRead(out _), "long dramatic pause inside unfinished verdict does not finalize playback");
        socket.Transcript(false, repairVerdict ? " nebo Knihovní bouře, už nezdržuj." : naturalVerdict
            ? " sedí k tvé odpovědi, tak už nezdržuj" : " sedí k tvé odpovědi. Tak už nezdržuj.", 5000, 6000); socket.Audio();
        await audioConsumed.Reader.ReadAsync(lifetime.Token);
        if (repairVerdict)
        {
            clock.Advance(3000);
            var repair = await socket.Command("session.instructions.append", lifetime.Token);
            Check(repair.Contains("Dokonči verdikt") && repair.Contains("Poslední slovo")
                && !playerRequests.Reader.TryRead(out _), "an ambiguous announcement receives one focused repair before any playback publication");
            socket.Transcript(false, "Jasanová hůlka Poslední slovo, tak už nezdržuj", 7000, 8000); socket.Audio();
            await audioConsumed.Reader.ReadAsync(lifetime.Token);
        }
        if (acknowledge)
        {
            await connection.SendAudioAsync(new byte[] { 0, 32, 0, 32 }, lifetime.Token);
            socket.Transcript(true, "Do", 6100, 6200);
            await consumed.Reader.ReadAsync(lifetime.Token);
            for (var frame = 0; frame < 72; frame++)
            {
                clock.Advance(500);
                await connection.SendAudioAsync(new byte[] { 0, 32, 0, 32 }, lifetime.Token);
            }
            Check(!socket.Sent.Any(s => s.Contains("session.close")), "continued guest speech suspends the terminal timeout and publication");
            await connection.SendAudioAsync(new byte[] { 0, 32, 0, 32 }, lifetime.Token);
            socket.Transcript(true, "bře, ukaž ji.", 6200, 6400);
            await consumed.Reader.ReadAsync(lifetime.Token);
        }
        socket.Emit(new { type = "session.usage.updated", usage = new { seconds = 12 } });
        // A provider context ACK has already arrived; only local drain may allow close.
        clock.Advance(1500);
        var checkpoint = await playerRequests.Reader.ReadAsync(lifetime.Token);
        Check(!socket.Sent.Any(s => s.Contains("session.close")), "append ACK and silence are not playback-done");
        Check(checkpoint.PcmBytes == pcmBytes && pcmBytes == (repairVerdict ? 12 : 8), "checkpoint covers actual transported PCM including a repaired announcement");
        if (interrupt)
        {
            await connection.SendAudioAsync(new byte[] { 0, 32, 0, 32 }, lifetime.Token);
            playerDone.SetResult(new(checkpoint.PlaybackId, checkpoint.PcmBytes, true));
            while (await states.Reader.ReadAsync(lifetime.Token) != "input_active") { }
            Check(!socket.Sent.Any(s => s.Contains("session.close")), "guest speech blocks a drained checkpoint before its transcript arrives");
            socket.Transcript(true, "Přestaň mluvit", 6100, 6500);
            await consumed.Reader.ReadAsync(lifetime.Token);
            await socket.Command("session.instructions.append", lifetime.Token);
            lifetime.Cancel();
            try { await run; throw new Exception("Interrupted verdict accepted"); } catch (OperationCanceledException) { }
            Check(!socket.Sent.Any(s => s.Contains("session.close")), "late local drain cannot finalize an interrupted verdict"); return;
        }
        playerDone.SetResult(new(checkpoint.PlaybackId, checkpoint.PcmBytes, true));
        var outcome = await run.WaitAsync(lifetime.Token);
        Check(outcome.Autonomous && outcome.PlaybackVerified && outcome.Decision!.Compartment == (correction ? 2 : 1)
            && outcome.Reply.Reply.Contains("nezdržuj") && outcome.Segments.Any(s => s.Speaker == "user" && s.StartMs == (fractionalTimeline ? 1100.25 : 1100)), "actual spoken transcript and timed raw user speech retained separately");
        Check(calls == (correction ? 2 : 1) && outcome.Decision!.DecisionRevision == outcome.DecisionRevision,
            "a split acknowledgment preserves the valid decision and its publication revision without another model call");
        Check(!socket.Sent.Any(s => s.Contains("response.create") || s.Contains("Replika ")), "native Live voice, no scripted response command");
        using var start = JsonDocument.Parse(socket.Sent.First());
        Check(start.RootElement.GetProperty("session").GetProperty("store").GetBoolean() == false
            && start.RootElement.GetProperty("session").GetProperty("delegation").GetProperty("type").GetString() == "client", "verified session.start wire fields");
    }
    private static async Task VerifyMissingContext(GptLiveOptions options)
    {
        using var clock = new ManualClock(); using var socket = new SocketFixture(); socket.BeforeDelivery = () => clock.Advance(9000);
        await using var connection = new GptLiveConnection(socket); using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var decisions = new WandDecisionService((_, _) => throw new Exception("Empty transcript paid call"));
        var run = new AutonomousLiveDialogue(connection, decisions, clock).RunAsync(Call(options, lifetime),
            (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.CompletedTask,
            (_, _) => throw new Exception("No terminal playback expected"), () => (63, 7, true), _ => Task.CompletedTask, lifetime.Token);
        await socket.Command("session.instructions.append", lifetime.Token);
        socket.Delegate("before-context", 2000);
        socket.Emit(new { type = "session.usage.updated", usage = new { seconds = 1 } });
        var recovery = await socket.Command("session.instructions.append", lifetime.Token);
        Check(recovery.Contains("before-context") && recovery.Contains("spolehlivý"), "missing transcript has bounded recovery with a known delegation ID");
        lifetime.Cancel();
        try { await run; } catch (OperationCanceledException) { }
        Check(!socket.Sent.Any(s => s.Contains("session.commentary.append") || s.Contains("session.close")), "delegation without transcript does not invent a task answer");
    }

    private static async Task VerifyDecisionFailure(GptLiveOptions options)
    {
        using var clock = new ManualClock(); using var socket = new SocketFixture();
        socket.BeforeDelivery = () => clock.Advance(50);
        await using var connection = new GptLiveConnection(socket);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var calls = 0;
        using var decisions = new WandDecisionService((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromException<WandDecisionResult>(new InvalidDataException("Incomplete decision."));
        });
        var timings = new List<(double Ms, WandDecisionStatus Status)>();
        var phases = new List<string>();
        var run = new AutonomousLiveDialogue(connection, decisions, clock).RunAsync(Call(options, lifetime),
            (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.CompletedTask,
            (_, _) => throw new Exception("Failed decision must not request playback"),
            () => (63, 7, true), _ => Task.CompletedTask, lifetime.Token,
            decisionTiming: (ms, status) => timings.Add((ms, status)), terminalState: phases.Add);
        await socket.Command("session.instructions.append", lifetime.Token);
        socket.Transcript(false, "Co uděláš, když se plán rozpadne?", 0, 1000);
        socket.Transcript(true, "Prostě mi nějakou vyber.", 1100, 2000);
        socket.Delegate("failing-decision", 2000);
        try { await run.WaitAsync(lifetime.Token); throw new Exception("Failed decision accepted"); }
        catch (InvalidDataException error)
        {
            Check(LiveFailureDiagnostics.Code(error) == "decision_incomplete", "decision failure is distinct from voice/socket failure");
        }
        Check(calls == 1 && timings.Count == 1 && timings[0].Status == WandDecisionStatus.Error && timings[0].Ms >= 0
            && phases.Contains("decision_running"), "failed work records its start, timing and failure without a paid replay");
        Check(!socket.Sent.Any(s => s.Contains("session.commentary.append") || s.Contains("session.close")),
            "a failed decision never publishes a catalog proposal");
    }

    private static async Task VerifyProviderFailure(GptLiveOptions options)
    {
        foreach (var (code, type, expected) in new[]
        {
            ("unknown_parameter", "invalid_request_error", "provider_unknown_parameter"),
            ("rate_limit_exceeded", "rate_limit_error", "provider_capacity"),
            ("arbitrary user/private data", "server_error", "provider_server_error"),
            ("arbitrary user/private data", "arbitrary private type", "provider_error_unknown")
        })
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { error = new
                { code, type, message = "Private user text, token and provider body", param = "Private parameter" } }));
            var error = LiveProviderFailure.Read(document.RootElement);
            Check(LiveFailureDiagnostics.Code(error) == expected && !error.ToString().Contains("Private"),
                "provider-controlled text is excluded from fixed technical failure categories");
        }
        Check(LiveFailureDiagnostics.Code(new WebSocketException()) == "live_socket_failed"
            && LiveFailureDiagnostics.Code(new InvalidDataException("arbitrary private data")) == "live_data_invalid",
            "transport and unknown local validation stay distinct without raw messages");
        using var clock = new ManualClock(); using var socket = new SocketFixture();
        await using var connection = new GptLiveConnection(socket);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var decisions = new WandDecisionService((_, _) => throw new Exception("Provider failure must not trigger a paid decision"));
        var run = new AutonomousLiveDialogue(connection, decisions, clock).RunAsync(Call(options, lifetime),
            (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.CompletedTask,
            (_, _) => throw new Exception("Provider failure must not request playback"),
            () => (63, 7, true), _ => Task.CompletedTask, lifetime.Token);
        await socket.Command("session.instructions.append", lifetime.Token);
        socket.Emit(new { type = "error", error = new { code = "server_error", type = "server_error", message = "Private provider content" } });
        try { await run.WaitAsync(lifetime.Token); throw new Exception("Provider error ignored"); }
        catch (LiveProviderFailure error) { Check(LiveFailureDiagnostics.Code(error) == "provider_server_error", "wire error reaches the endpoint with a safe cause"); }
        Check(!socket.Sent.Any(s => s.Contains("session.commentary.append") || s.Contains("session.close")),
            "provider failure cannot invent successful completion");
    }

    private static WandDecisionResult Proposal(WandDecisionInput i, int compartment)
    {
        var evidence = i.Evidence.Last();
        return new(WandDecisionStatus.ValidProposal, compartment,
            [new(evidence.Reference, evidence.Text.Length <= 70 ? evidence.Text : evidence.Text[..70], "Vazba na doloženou odpověď hosta.")], "", i.Revision, i.AvailabilityRevision, 10);
    }
    private static RealtimeCall Call(GptLiveOptions options, CancellationTokenSource token)
    {
        var request = new RealtimeTurnRequest(Guid.NewGuid(), Guid.NewGuid(), 0, 63, 7, PartyWandCatalog.Version, true, "", null, "marin", 1);
        var context = new RealtimeTurnContext(request, true, "", []) { PersonalityId = PersonalityCatalog.OllivanderId, LivePrompt = LivePromptSnapshot.Create(PersonalityCatalog.OllivanderId, options) };
        return new("fixture-op", new("device", request.BootId, Guid.NewGuid().ToString("D"), request.ClientTurnId, 0, 7), context, null, token);
    }
    private static async Task VerifyPlaybackRegistry()
    {
        using var socket = new SocketFixture(); await using var connection = new GptLiveConnection(socket);
        using var token = new CancellationTokenSource();
        using var session = new RealtimeLiveSession("d", "c", connection, token, true);
        var request = new LivePlaybackRequest(Guid.NewGuid(), 48000, 2); var drain = session.PrepareCheckpoint(request);
        Check(!session.ConfirmCheckpoint(request.PlaybackId, 47000, true) && !session.ConfirmCheckpoint(request.PlaybackId, 48000, false) && !drain.IsCompleted,
            "undrained or incomplete local PCM cannot confirm completion");
        Check(session.ConfirmCheckpoint(request.PlaybackId, 48000, true) && (await drain).Drained,
            "real local checkpoint acknowledgement");
        Check(session.ConfirmCheckpoint(request.PlaybackId, 48000, true) && !session.ConfirmCheckpoint(request.PlaybackId, 2, true), "lost ACK can be retried idempotently with exact body");
        var pending = new LivePlaybackRequest(Guid.NewGuid(), 96000, 3);
        var oldDrain = session.PrepareCheckpoint(pending);
        session.CancelCheckpoint();
        Check(!(await oldDrain).Drained && !session.ConfirmCheckpoint(pending.PlaybackId, pending.PcmBytes, true),
            "cancelled old audio-completed cannot complete a new response");
    }
    private static void Append(LiveConversationProjection p, bool input, string text, int start, int end)
    { using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { type = input ? "session.input_transcript.delta" : "session.output_transcript.delta", delta = text, start_ms = start, end_ms = end, event_id = Guid.NewGuid().ToString("N") })); p.Append(doc.RootElement, input); }
    private static void Check(bool result, string detail) { if (!result) throw new Exception("Autonomous Live: " + detail); }

    private sealed class SocketFixture : WebSocket
    {
        private readonly Channel<byte[]> events = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<string> commands = Channel.CreateUnbounded<string>();
        internal readonly ConcurrentQueue<string> Sent = new();
        internal Action? BeforeDelivery;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() => events.Writer.TryComplete();
        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken t) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken t) => Task.CompletedTask;
        internal void Emit(object item) => events.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(item));
        internal void Transcript(bool input, string text, double start, double end) => Emit(new { type = input ? "session.input_transcript.delta" : "session.output_transcript.delta", delta = text, start_ms = start, end_ms = end, event_id = Guid.NewGuid().ToString("N") });
        internal void Delegate(string id, double ms) => Emit(new { type = "session.delegation.created", delegation = new { id, target = "client" }, offset_ms = ms });
        internal void Audio() => Emit(new { type = "session.output_audio.delta", delta = Convert.ToBase64String(new byte[] { 0, 32, 0, 32 }) });
        internal async Task<string> Command(string type, CancellationToken token)
        { while (true) { var text = await commands.Reader.ReadAsync(token); using var doc = JsonDocument.Parse(text); if (doc.RootElement.GetProperty("type").GetString() == type) return text; } }
        public override Task SendAsync(ArraySegment<byte> bytes, WebSocketMessageType type, bool end, CancellationToken token)
        {
            var text = System.Text.Encoding.UTF8.GetString(bytes); Sent.Enqueue(text); commands.Writer.TryWrite(text);
            using var doc = JsonDocument.Parse(text); var root = doc.RootElement; var kind = root.GetProperty("type").GetString()!;
            if (kind == "session.start") Emit(new { type = "session.started", client_event_id = root.GetProperty("event_id").GetString(), session = new { model = "gpt-live-1" } });
            else if (kind.EndsWith(".append") && kind != "session.input_audio.append") Emit(new { type = kind.Replace(".append", ".appended"), client_event_id = root.GetProperty("event_id").GetString() });
            else if (kind == "session.close") Emit(new { type = "session.closed", client_event_id = root.GetProperty("event_id").GetString(), reason = "close_requested", usage = new { seconds = 12 } });
            return Task.CompletedTask;
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        { var next = await events.Reader.ReadAsync(token); BeforeDelivery?.Invoke(); next.AsSpan().CopyTo(buffer.AsSpan()); return new(next.Length, WebSocketMessageType.Text, true); }
    }

    private sealed class ManualClock : TimeProvider, IDisposable
    {
        private long milliseconds;
        private readonly List<Timer> timers = [];
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(milliseconds);
        public override long GetTimestamp() => Interlocked.Read(ref milliseconds);
        public override long TimestampFrequency => 1000;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan due, TimeSpan period)
        { var timer = new Timer(this, callback, state); timer.Change(due, period); lock (timers) timers.Add(timer); return timer; }
        internal void Advance(int ms)
        {
            Interlocked.Add(ref milliseconds, ms); Timer[] items; lock (timers) items = timers.ToArray();
            foreach (var timer in items) timer.Fire(milliseconds);
        }
        public void Dispose() { lock (timers) foreach (var timer in timers) timer.Dispose(); }
        private sealed class Timer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private long next = long.MaxValue, period;
            public bool Change(TimeSpan due, TimeSpan interval) { next = due < TimeSpan.Zero ? long.MaxValue : clock.milliseconds + (long)due.TotalMilliseconds; period = (long)interval.TotalMilliseconds; return true; }
            internal void Fire(long now) { if (now < next) return; next = period > 0 ? now + period : long.MaxValue; callback(state); }
            public void Dispose() => next = long.MaxValue;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
