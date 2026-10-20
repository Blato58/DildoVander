using Kufr.Backend.Features.Character;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Show;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

internal static class ShowDirectorTests
{
    internal static void Run()
    {
        ContentAndScenes();
        MoodBoundsAndHysteresis();
        CommandScopesAndRestart();
        ClosedEveningRejectsPreparedVisitAndDuel();
        PublicOriginScopeAndWandHistory();
        CancellationPriority();
        LoreAndVisitLifecycle();
        SceneExecutionBoundaries();
        TypedCueAdapters();
        ImmutableFinaleProposalBindings();
        ActualSelectionOwnerBindsFinale();
        AmbientVirtualThirtyMinutes();
        Console.WriteLine("Show director deterministic tests passed; no provider/audio/device acceptance claim.");
    }

    private static void ClosedEveningRejectsPreparedVisitAndDuel()
    {
        using var fixture = new Fixture();
        var show = fixture.Create();
        Check(fixture.Memory.CaptureShowVisit("show-device", "prepared") == fixture.EveningId,
            "visit prepared before closure");
        fixture.Memory.EndShowEvening("show-device", fixture.EveningId);
        var rejected = false;
        try { using var visit = show.BeginVisit("show-device", fixture.EveningId, "prepared", "generation"); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected && !show.TryOccupy("show-device", fixture.EveningId, "late-duel"),
            "closing rejects both a previously prepared visit and a new duel");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = fixture.DatabasePath,
            ["Keepsake:DataRoot"] = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "archive")
        }).Build();
        var identities = new DuelIdentitySource(new Kufr.Backend.Features.Keepsakes.KeepsakeStore(config, fixture.Clock), fixture.Memory);
        Check(!identities.IsOpenEvening("show-device", fixture.EveningId), "closed pointer is not open duel admission");
    }

    private static void PublicOriginScopeAndWandHistory()
    {
        using var fixture = new Fixture();
        var show = fixture.Create();
        Check(show.Read("show-device").PublicOrigin == "https://show.test"
            && show.Read("other-device").PublicOrigin is null, "public links belong only to configured guest device");
        var actual = new HashSet<string>();
        var actualIds = new HashSet<string>();
        for (var index = 0; index < 18; index++)
        {
            var generation = "voice-" + index;
            using var visit = show.BeginVisit("show-device", fixture.EveningId, "guest-" + index, generation);
            var state = show.Read("show-device");
            show.Command("show-device", new(Guid.NewGuid().ToString("D"), state.Revision, "scene-twist",
                state.SessionId, generation, ShowSceneLibrary.Archetypes[index % ShowSceneLibrary.Archetypes.Length]));
            var before = show.Read("show-device").RecentWandLineIds?.Length ?? 0;
            var lines = show.OfferWandRepertoire("show-device", generation, 1, 7);
            Check(lines.Length == 3 && (show.Read("show-device").RecentWandLineIds?.Length ?? 0) == before,
                "offered repertoire never counts as observed audio");
            show.RecordWandSpeechProxy("show-device", "stale-generation", 1, 7, string.Join(" ", lines.Select(line => line.Text)));
            Check((show.Read("show-device").RecentWandLineIds?.Length ?? 0) == before, "stale speech cannot record lines");
            show.RecordWandSpeechProxy("show-device", generation, 1, 7, string.Join(" ", lines.Select(line => line.Text)));
            foreach (var line in lines) { actual.Add(line.Kind); actualIds.Add(line.Id); }
            Check(show.Read("show-device").RecentWandLineIds!.Length <= 24, "anonymous line history is bounded");
            visit.Leave();
        }
        Check(new[] { "introduction", "acceptance", "fictional-refusal", "overlooked", "rivalry", "closing" }
            .All(actual.Contains), "all authored categories have a runtime consumer");
        Check(actualIds.Count == 9, "alternative introduction, acceptance and refusal lines rotate through observed history");
        Check(fixture.Create().Read("show-device").RecentWandLineIds!.Length > 0, "played line history survives restart");
        using var finalVisit = show.BeginVisit("show-device", fixture.EveningId, "phrase-proof", "proof");
        show.OfferWandRepertoire("show-device", "proof", 2, 8);
        var old = show.Read("show-device").RecentWandLineIds!.ToArray();
        show.RecordWandSpeechProxy("show-device", "proof", 2, 8, "Model řekl něco úplně jiného.");
        Check(show.Read("show-device").RecentWandLineIds!.SequenceEqual(old), "omitted plans remain unobserved");
        var offered = show.OfferWandRepertoire("show-device", "proof", 2, 9);
        show.RecordWandSpeechProxy("show-device", "proof", 2, 8, offered[0].Text);
        Check(show.Read("show-device").RecentWandLineIds!.SequenceEqual(old), "old selection revision cannot count a line");
        var split = new[] { offered[0].Text[..5], offered[0].Text[5..] };
        show.RecordWandSpeechProxy("show-device", "proof", 2, 9, string.Concat(split).ToUpperInvariant());
        Check(show.Read("show-device").RecentWandLineIds!.Last() == offered[0].Id
            && !show.Read("show-device").RecentWandLineIds!.Contains(offered[1].Id), "only matching line of split transcript is observed");
        var cancelled = show.OfferWandRepertoire("show-device", "proof", 3, 10);
        show.CancelWandRepertoire("show-device", "proof");
        show.RecordWandSpeechProxy("show-device", "proof", 3, 10, string.Join(" ", cancelled.Select(line => line.Text)));
        Check(!show.Read("show-device").RecentWandLineIds!.Contains(cancelled[0].Id), "cancelled plan cannot become observed");
        finalVisit.Leave();
    }

    private static void ContentAndScenes()
    {
        ShowRepertoire.Validate();
        Check(ShowRepertoire.Wands.Count == 6 && ShowRepertoire.Ambient.Length >= 12, "catalog content coverage");
        foreach (var id in ShowSceneLibrary.Archetypes)
        {
            foreach (var variant in new[] { (false, true, "normal"), (true, true, "express"), (false, false, "no-motor") })
            {
                var scene = ShowSceneLibrary.Create(id, "generation", variant.Item1, variant.Item2, FinaleProfile.Blocked);
                ShowSceneLibrary.Validate(scene);
                Check(scene.Variant == variant.Item3 && scene.Cues.All(c => c.SessionGeneration == "generation"), "all generation-bound variants");
                Check(scene.Cues.All(c => c.Kind != "PresentSelected"), "scene cannot grant physical authority or bypass Blocked");
            }
        }
        var recent = new List<string>();
        for (var index = 0; index < 40; index++)
        {
            var selected = ShowSceneLibrary.Select(recent, "seed-" + index);
            Check(!recent.TakeLast(3).Contains(selected), "scene avoids three recent observed archetypes");
            recent.Add(selected);
        }
        Check(recent.Distinct().Count() >= 8, "seeded variety covers at least eight archetypes");
        var session = new CharacterSession("g", new(true, CharacterTone.Adult));
        session.AcceptInput("s", 1, "bez nadávek");
        session.ApplyOperatorStyle("chaos", "express");
        Check((int)session.Tone <= (int)CharacterTone.Dry && session.Tempo == CharacterTempo.Express, "operator cannot revoke guest tone cap");
        Check(session.Policy.FinaleProfile == FinaleProfile.Blocked, "style preserves frozen finale gate");
    }

    private static void MoodBoundsAndHysteresis()
    {
        foreach (var mood in MoodReactor.Names)
        {
            var value = MoodReactor.Set(mood);
            for (var index = 0; index < 200; index++) { value = MoodReactor.Advance(value, TimeSpan.FromSeconds(10), true, true); }
            Check(new[] { value.Energy, value.Irritation, value.Confidence, value.Suspicion }.All(v => v is >= 0 and <= 100), "mood bounded");
        }
        var locked = MoodReactor.Set("panicking", true);
        Check(MoodReactor.Advance(locked, TimeSpan.FromDays(1), true, true) == locked, "evening lock resists automatic updates");
        var near = new TrunkMood(50, 65, 55, 20, "irritated");
        Check(MoodReactor.Advance(near, TimeSpan.Zero).Dominant == "irritated", "hysteresis retains near threshold");
        Check(MoodReactor.Advance(near, TimeSpan.FromHours(1)).Dominant == "amused", "decay restores baseline");
    }

    private static void CommandScopesAndRestart()
    {
        using var fixture = new Fixture();
        var show = fixture.Create();
        var initial = show.Read("show-device");
        Check(!show.IsKnownDevice("unknown-device"), "configured device scope");
        var mood = new ShowCommand("mood-lock", initial.Revision, "mood-evening", Value: "suspicious");
        var accepted = show.Command("show-device", mood);
        Check(accepted.Status == "accepted" && accepted.State.Mood.Locked, "evening mood lock");
        Check(show.Command("show-device", mood).Status == "replayed", "same command replays without effect");
        Check(show.Command("show-device", mood with { Value = "bored" }).Code == "idempotency_conflict", "changed command rejected");
        Check(show.Command("show-device", new("stale", 0, "quiet", Value: "on")).Code == "stale_revision", "optimistic concurrency");
        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        Check(show.Command("show-device", mood).Status == "expired", "expired duplicate never executes again");
        using (var visit = show.BeginVisit("show-device", fixture.EveningId, "session", "generation"))
        {
            var current = show.Read("show-device");
            Check(!show.TryOccupy("show-device", fixture.EveningId, "duel"), "visit excludes duel");
            Check(!show.TryCloseEvening("show-device", current.Revision, fixture.EveningId, () => throw new Exception()), "active visit excludes close");
            var wrong = new ShowCommand("wrong-generation", current.Revision, "tempo", "session", "old", "express");
            Check(show.Command("show-device", wrong).Code == "stale_session", "generation must match");
            Check(show.Command("show-device", wrong with { CommandId = "express", SessionGeneration = "generation" }).Status == "accepted", "scoped express accepted");
            Check(show.TakeControls("show-device", "old").Length == 0 && show.TakeControls("show-device", "generation").Length == 1, "owner drains controls once");
            show.RecordSceneSpeechProxy("show-device", "generation");
            visit.Complete();
        }
        var after = show.Read("show-device");
        Check(after.Phase == "idle" && after.RecentSceneIds?.Length == 1, "completed visit records bounded observed proxy");
        var restarted = fixture.Create();
        Check(restarted.Read("show-device").Mood.Dominant == "suspicious" && restarted.Read("show-device").RecentSceneIds?.Length == 1, "mood and variety survive restart");
        Check(restarted.TryOccupy("show-device", fixture.EveningId, "duel"), "exclusive duel occupancy");
        var stop = restarted.Command("show-device", new("stop", restarted.Read("show-device").Revision, "stop"));
        Check(stop.State.Phase == "stopped" && !restarted.IsOccupationCurrent("show-device", "duel"), "director stop invalidates duel");
        restarted.Release("show-device", "duel");
        Check(restarted.Read("show-device").Phase == "stopped", "late duel completion cannot clear stop");
        restarted.Command("show-device", new("recover", restarted.Read("show-device").Revision, "recover"));
        _ = restarted.BeginVisit("show-device", fixture.EveningId, "crashed-session", "crashed-generation");
        var recoveredProcess = fixture.Create();
        Check(recoveredProcess.Read("show-device") is { Phase: "interrupted", SessionId: null, AmbientEnabled: false }, "restart cannot restore old live owner");
    }

    private static void AmbientVirtualThirtyMinutes()
    {
        using var fixture = new Fixture();
        var show = fixture.Create();
        show.Command("show-device", new("enable", show.Read("show-device").Revision, "ambient", Value: "on"));
        var spoken = new List<(DateTimeOffset At, string Id)>();
        for (var second = 1; second <= 1800; second++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            var cue = show.TickAmbient("show-device", true, true);
            if (cue is { Kind: "ActorDirection" })
            {
                Check(second >= 120 && cue.DeadlineMs <= 8000, "speech idle threshold and bounded duration");
                spoken.Add((fixture.Clock.GetUtcNow(), cue.Value));
                show.ConfirmAmbientPlayback("show-device", cue.CueId);
                var window = spoken.Count(s => fixture.Clock.GetUtcNow() - s.At < TimeSpan.FromMinutes(10));
                Check(window <= 3, "three scheduled speech cues per sliding ten minutes");
            }
        }
        Check(spoken.Count >= 6 && spoken.Select(s => s.Id).Distinct().Count() >= 5, "thirty-minute variety");
        Check(spoken.Zip(spoken.Skip(1), (a, b) => b.At - a.At).All(d => d >= TimeSpan.FromSeconds(180)), "speech cooldown");
        var quiet = show.Command("show-device", new("quiet", show.Read("show-device").Revision, "quiet", Value: "on"));
        Check(quiet.State.PendingCue is null, "quiet cancels queued ambient without provider");
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        Check(show.TickAmbient("show-device", true, true) is null, "quiet suppresses future cue");
        show.Command("show-device", new("unquiet", show.Read("show-device").Revision, "quiet", Value: "off"));
        using (var visit = show.BeginVisit("show-device", fixture.EveningId, "guest", "g"))
        {
            Check(show.Read("show-device").PendingCue is null && show.TickAmbient("show-device", true, true) is null, "visit cancels ambient generation");
            visit.Complete();
        }
        fixture.Clock.Advance(TimeSpan.FromSeconds(29));
        Check(show.TickAmbient("show-device", true, true) is null, "postvisit grace");
        fixture.Clock.Advance(TimeSpan.FromSeconds(100));
        Check(show.TickAmbient("show-device", false, true) is null, "unresolved movement forbids ambient");
        Check(show.TickAmbient("show-device", true, false)?.Kind != "ActorDirection", "audio loss has silent fallback");
    }

    private static void CancellationPriority()
    {
        using var fixture = new Fixture();
        var show = fixture.Create();
        using var visit = show.BeginVisit("show-device", fixture.EveningId, "session", "generation");
        void Fill(int ttl = 30)
        {
            for (var index = 0; index < 32; index++)
            {
                var result = show.Command("show-device", new(Guid.NewGuid().ToString("N"), show.Read("show-device").Revision,
                    "tempo", "session", "generation", "express", ttl));
                Check(result.Status == "accepted", "fill bounded control queue");
            }
        }
        Fill();
        Check(show.Command("show-device", new("over-capacity", show.Read("show-device").Revision,
            "tempo", "session", "generation", "express")).Code == "command_queue_full", "ordinary queue bound");
        var cancel = show.Command("show-device", new("cancel-priority", show.Read("show-device").Revision,
            "cancel-speech", "session", "generation"));
        Check(cancel.Status == "accepted" && show.TakeControls("show-device", "generation") is [{ Kind: "cancel-speech" }],
            "speech cancellation preempts saturated queue");
        Fill(1);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        Check(show.Command("show-device", new("after-expiry", show.Read("show-device").Revision,
            "tempo", "session", "generation", "express")).Status == "accepted", "expired controls do not consume capacity");
        _ = show.TakeControls("show-device", "generation");
        using (var connection = new SqliteConnection("Data Source=" + fixture.DatabasePath))
        {
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO show_director_commands VALUES('show-device',$id,'quota',$expires,$created,'{}')";
            var id = insert.Parameters.Add("$id", SqliteType.Text);
            insert.Parameters.AddWithValue("$expires", fixture.Clock.GetUtcNow().AddHours(1).ToString("O"));
            insert.Parameters.AddWithValue("$created", fixture.Clock.GetUtcNow().ToString("O"));
            for (var index = 0; index < 4096; index++) { id.Value = "quota-" + index; insert.ExecuteNonQuery(); }
            transaction.Commit();
        }
        Check(show.Command("show-device", new("quota-ordinary", show.Read("show-device").Revision,
            "tempo", "session", "generation", "express")).Code == "command_retention_limit", "ordinary receipt bound");
        Check(show.Command("show-device", new("quota-cancel", show.Read("show-device").Revision,
            "cancel-speech", "session", "generation")).Status == "accepted", "cancel bypasses ordinary receipt quota");
        Check(show.Command("show-device", new("quota-leave", show.Read("show-device").Revision,
            "guest-leave", "session", "generation")).Status == "accepted", "guest leave bypasses ordinary receipt quota");
        var stop = show.Command("show-device", new("quota-stop", 0, "stop"));
        Check(stop.Status == "accepted" && stop.State.DeviceStopRevision == 1,
            "stop bypasses ordinary quota and stale revision, with a distinct device revision");
        Check(show.Command("show-device", new("quota-stop", 0, "stop")).State.DeviceStopRevision == 1,
            "idempotent stop replay does not issue another device stop");
        Check(show.TakeControls("show-device", "generation") is [{ Kind: "stop" }], "stop preempts earlier cancellation too");
        var committed = false;
        Check(!show.TryCommitCharacterFinale("show-device", "generation", () => committed = true) && !committed,
            "stopped director cannot commit a late physical proposal");
    }

    private static void LoreAndVisitLifecycle()
    {
        using var fixture = new Fixture();
        var show = fixture.Create();
        using (var first = show.BeginVisit("show-device", fixture.EveningId, "first-visit", "first-generation"))
        { first.Complete(); }
        using (var second = show.BeginVisit("show-device", fixture.EveningId, "second-visit", "second-generation"))
        {
            Check(second.Lore.Motifs.Count is > 0 and <= 3, "visit captures bounded anonymous lore");
            var direction = show.Direction("show-device", "second-generation", CharacterPhase.Responding, false, FinaleProfile.Blocked);
            Check(direction.Contains("Večerní anonymní fikce", StringComparison.Ordinal), "owner receives safe lore direction");
            Check(!show.Direction("show-device", "second-generation", CharacterPhase.Deciding, false, FinaleProfile.Blocked)
                .Contains("Večerní anonymní fikce", StringComparison.Ordinal), "lore offered at most once");
            Check(fixture.Memory.BuildEveningLore("show-device", fixture.EveningId).Motifs.Any(m => m.Id == "ministry_queue"),
                "planning a motif is not a documented use");
            show.ConfirmSpeechProxy("show-device", "old-generation");
            Check(fixture.Memory.BuildEveningLore("show-device", fixture.EveningId).Motifs.Any(m => m.Id == "ministry_queue"),
                "old speech cannot consume new visit lore");
            show.ConfirmSpeechProxy("show-device", "second-generation");
            show.ConfirmSpeechProxy("show-device", "second-generation");
            Check(!fixture.Memory.BuildEveningLore("show-device", fixture.EveningId).Motifs.Any(m => m.Id == "ministry_queue"),
                "observed speech proxy activates persisted joke cooldown once");
            second.Leave();
        }
        var snapshot = fixture.Memory.ReadChronicle("show-device", fixture.EveningId)!;
        Check(snapshot.CompletedVisits == 1 && snapshot.InterruptedVisits == 1 && snapshot.ConfirmedPresentations == 0,
            "dialogue closure and guest departure emit distinct deduplicated facts without physical claims");
    }

    private static void SceneExecutionBoundaries()
    {
        var clock = new Clock();
        var definition = ShowSceneLibrary.Create("approved-silence", "g", false, false, FinaleProfile.Blocked);
        var scene = new ShowSceneExecution(definition, clock, selectionValid: true);
        Check(scene.Tick().Cues.Any(c => c.Kind == "ActorDirection" && c.Status == "submitted"), "scene submits actor direction");
        Check(!scene.PlaybackConfirmed().PausePending, "missing audio-start evidence cannot synthesize playback");
        var started = scene.TerminalAudioStarted();
        Check(started.Cues.Any(c => c.Status == "observed_proxy"), "audio sender advances submitted direction proxy");
        Check(scene.TerminalAudioStarted().Revision == started.Revision, "duplicate audio-start event idempotent");
        Check(scene.PlaybackConfirmed().PausePending, "correlated drained player starts actual monotonic pause");
        clock.Advance(TimeSpan.FromMilliseconds(1199));
        Check(scene.Tick().PausePending, "pause remains until finite duration");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Check(scene.Tick() is { Completed: true, PausePending: false }, "pause completes final local semantic beat");
        var cancelled = new ShowSceneExecution(definition, clock, true);
        cancelled.TerminalAudioStarted();
        cancelled.PlaybackConfirmed();
        Check(cancelled.Cancel() is { Cancelled: true, PausePending: false }, "stop cancels optional scene immediately");
        clock.Advance(TimeSpan.FromHours(1));
        Check(cancelled.PlaybackConfirmed().Cancelled, "late playback cannot revive cancelled scene");
        var unsupported = ShowSceneLibrary.Create("coronation", "g", false, true, FinaleProfile.Blocked);
        unsupported = unsupported with { Cues = unsupported.Cues.Concat(new[]
        {
            new ShowCue("unsupported-led", "LedPreset", "scene_started", 0, 1000, "drop", "g", "unconfigured"),
            new ShowCue("unsupported-clip", "PlayLocalClip", "scene_started", 0, 1000, "drop", "g", "unconfigured")
        }).ToArray() };
        var unavailable = new ShowSceneExecution(unsupported, clock, true);
        Check(unavailable.Tick().Cues.Where(c => c.CueId.StartsWith("unsupported-")).All(c => c.Status == "unsupported"),
            "no capability adapter means explicit unsupported cues");
        using var fixture = new Fixture();
        var show = fixture.Create();
        using var visit = show.BeginVisit("show-device", fixture.EveningId, "scene-owner", "scene-generation");
        show.BeginFinale("show-device", "scene-generation", false, FinaleProfile.Blocked, true, 42);
        Check(show.OnTerminalAudioStarted("show-device", "old-generation", 42) is null
            && show.OnPlaybackConfirmed("show-device", "scene-generation", 41) is null, "owner hooks reject old generation and decision");
        show.OnTerminalAudioStarted("show-device", "scene-generation", 42);
        Check(show.OnPlaybackConfirmed("show-device", "scene-generation", 42)?.PausePending == true,
            "existing owner adapter executes real bounded scene pause");
        show.CancelScene("show-device", "scene-generation");
        Check(show.TickScene("show-device", "scene-generation")?.Cancelled == true, "owner cancellation is sticky");
        visit.Complete();
    }

    private static void TypedCueAdapters()
    {
        using var fixture = new Fixture();
        var show = fixture.Create();
        var states = new Dictionary<string, string>();
        var submitted = new List<ShowCue>();
        show.ConfigureSceneCueAdapter((device, generation, cue) =>
        {
            Check(Task.Run(() => show.Read(device)).Wait(TimeSpan.FromSeconds(2)), "device adapter runs outside coordinator lock");
            submitted.Add(cue);
            states[cue.CueId] = "queued";
            return "queued";
        }, (_, _, id) => states.GetValueOrDefault(id));
        using var visit = show.BeginVisit("show-device", fixture.EveningId, "typed", "typed-generation");
        show.BeginFinale("show-device", "typed-generation", false, FinaleProfile.Limited, true, 9, 2);
        show.DispatchSceneCues("show-device", "typed-generation");
        Check(submitted.Any(c => c.Kind == "LedPreset") && submitted.Any(c => c.Kind == "VibratePreset"),
            "allowlisted light and vibration reach device adapter");
        Check(submitted.All(c => c.TargetSlot == 2), "device presets retain the validated selection slot");
        foreach (var cue in submitted) { states[cue.CueId] = "accepted"; }
        Check(show.TickScene("show-device", "typed-generation")!.Cues.Any(c => c.Kind == "LedPreset" && c.Status == "accepted"),
            "admission alone is not physical completion");
        foreach (var cue in submitted) { states[cue.CueId] = "completed"; }
        show.OnTerminalAudioStarted("show-device", "typed-generation", 9);
        show.OnPlaybackConfirmed("show-device", "typed-generation", 9);
        show.DispatchSceneCues("show-device", "typed-generation");
        var clip = submitted.Single(c => c.Kind == "PlayLocalClip");
        states[clip.CueId] = "unsupported_capability";
        Check(show.TickScene("show-device", "typed-generation")!.Cues.Any(c => c.CueId == clip.CueId && c.Status == "unsupported"),
            "missing approved bank is an explicit clip capability fallback");
        var epoch = show.Read("show-device").CancellationEpoch;
        show.CancelScene("show-device", "typed-generation");
        Check(show.Read("show-device").CancellationEpoch > epoch, "cancelled response fences in-flight device cues");
        states[clip.CueId] = "completed";
        Check(show.TickScene("show-device", "typed-generation")!.Cancelled, "late completion cannot revive scene");

        var definition = ShowSceneLibrary.Create("coronation", "p", false, true, FinaleProfile.Limited);
        var execution = new ShowSceneExecution(definition, fixture.Clock, true);
        Check(execution.PresentationProposalBound("op", "proposal").Cues.Single(c => c.Kind == "PresentSelected")
            is { Status: "accepted", Reason: "existing-operation:op/proposal:proposal" }, "presentation cue binds existing operation only");
        Check(execution.PresentationOutcomeObserved().Cues.Single(c => c.Kind == "PresentSelected").Status == "completed",
            "existing authenticated outcome completes the attached cue without new motion dispatcher");
        var late = new ShowSceneExecution(ShowSceneLibrary.Create("coronation", "late", false, false, FinaleProfile.Blocked),
            fixture.Clock, true, _ => "completed");
        late.TakeAdapterCues();
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        Check(late.Tick().Cues.Single(c => c.Kind == "LedPreset").Status == "dropped",
            "a late device completion cannot outrun the cue deadline");
        visit.Leave();
    }

    private static void ImmutableFinaleProposalBindings()
    {
        using var fixture = new Fixture();
        var show = fixture.Create();
        using (var visit = show.BeginVisit("show-device", fixture.EveningId, "finale-session", "finale-generation"))
        {
            show.BeginFinale("show-device", "finale-generation", false, FinaleProfile.Limited, true, 7);
            show.CaptureFinaleAnchors("show-device", "finale-generation",
                [new("anchor-1", "source-1", 3, 4)]);
            visit.Complete();
        }
        var proposal = new SelectionProposal(Guid.NewGuid().ToString("D"), "finale-session", Guid.NewGuid(),
            PartyWandCatalog.Version, 12, 1, "PRIVATE REASON MUST NOT BE PERSISTED", Guid.NewGuid(), 5,
            fixture.Clock.GetUtcNow().AddSeconds(60));
        var first = show.BindFinaleProposal("show-device", "finale-operation", proposal, "finale-generation",
            7, "dv01-adult-v1", FinaleProfile.Limited, fixture.EveningId);
        Check(first is { Status: "bound", Plan: { Locked: false, SlotId: 1 } }, "actual accepted proposal binds plan");
        Check(show.Read("show-device").SceneExecution!.Cues.Single(c => c.Kind == "PresentSelected").Status == "accepted",
            "normal completed dialogue disposal retains metadata handoff for the actual publication owner");
        var plan = first.Plan!;
        Check(plan.ProposalId == proposal.ProposalId && plan.OperationId == "finale-operation"
            && plan.CatalogWandId == PartyWandCatalog.Available(63).Single(w => w.Compartment == 1).StableId
            && plan.CatalogVersion == proposal.CatalogVersion && plan.DeviceBootId == proposal.BootId
            && plan.DeviceGeneration == proposal.Generation && plan.ClientTurnId == proposal.TurnId
            && plan.SelectionRevision == 7 && plan.Deadline == proposal.ExpiresAt
            && plan.ApprovedAnchorReferences is [{ AnchorId: "anchor-1" }], "immutable exact proposal/catalog/provenance snapshot");
        Check(!System.Text.Json.JsonSerializer.Serialize(plan).Contains("PRIVATE REASON", StringComparison.Ordinal),
            "technical plan contains references, not visitor text");
        Check(show.BindFinaleProposal("show-device", "finale-operation", proposal, "finale-generation", 7,
            "dv01-adult-v1", FinaleProfile.Limited, fixture.EveningId).Status == "replayed", "same binding idempotent");
        Check(show.BindFinaleProposal("show-device", "finale-operation", proposal with { Compartment = 2 }, "finale-generation", 7,
            "dv01-adult-v1", FinaleProfile.Limited, fixture.EveningId).Status == "conflict", "same proposal cannot change slot");
        Check(show.BindFinaleProposal("show-device", "finale-operation", proposal with { BootId = Guid.NewGuid() }, "finale-generation", 7,
            "dv01-adult-v1", FinaleProfile.Limited, fixture.EveningId).Status == "conflict", "same proposal cannot change boot snapshot");
        Check(show.BindFinaleProposal("show-device", "wrong-catalog", proposal with { CatalogVersion = "wrong" }, "finale-generation", 7,
            "dv01-adult-v1", FinaleProfile.Limited, fixture.EveningId).Status == "rejected", "catalog revision must be authoritative");
        foreach (var profile in new[] { FinaleProfile.Blocked, FinaleProfile.Named })
        {
            Check(show.BindFinaleProposal("show-device", "profile-" + profile, proposal, "finale-generation", 7,
                "dv01-adult-v1", profile, fixture.EveningId).Code == "finale_profile_not_publishable", "plan cannot open blocked or named gate");
        }
        var outcome = Guid.NewGuid().ToString("D");
        Check(!show.ObserveFinaleOutcome("show-device", "finale-operation", "wrong-proposal", outcome, "completed_by_timing")
            && !show.ObserveFinaleOutcome("show-device", "finale-operation", proposal.ProposalId, outcome, "sensor_verified"),
            "unmatched or unsupported physical evidence cannot lock plan");
        Check(show.ObserveFinaleOutcome("show-device", "finale-operation", proposal.ProposalId, outcome, "completed_by_timing"),
            "existing trusted timing outcome locks authoritative identity");
        Check(show.ObserveFinaleOutcome("show-device", "finale-operation", proposal.ProposalId, outcome, "completed_by_timing")
            && !show.ObserveFinaleOutcome("show-device", "finale-operation", proposal.ProposalId, "different-outcome", "completed_by_timing"),
            "outcome repetition cannot replace locked identity");
        var restarted = fixture.Create();
        Check(restarted.Read("show-device").FinalePlan is { Locked: true, State: "timed-completed" }
            && restarted.ReadFinalePlan("show-device", "finale-operation")?.FinaleId == plan.FinaleId,
            "locked immutable plan persists across restart");
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        Check(restarted.BindFinaleProposal("show-device", "finale-operation", proposal, "finale-generation", 7,
            "dv01-adult-v1", FinaleProfile.Limited, fixture.EveningId).Plan?.Locked == true,
            "expired replay returns original locked plan without rebuilding it");
        Check(restarted.BindFinaleProposal("show-device", "expired-new-operation", proposal, "finale-generation", 7,
            "dv01-adult-v1", null, fixture.EveningId).Code == "finale_deadline_expired", "new expired plan cannot bind");
    }

    private static void ActualSelectionOwnerBindsFinale()
    {
        using var fixture = new Fixture();
        var show = fixture.Create();
        var store = fixture.CreateConversationStore(show);
        const string device = "debug-server-admin";
        var boot = Guid.NewGuid();
        var created = store.Create(device, Guid.NewGuid(), "create-finale"u8,
            new(device, boot, 0, PartyWandCatalog.Version, "cs-CZ", new(false, false)));
        Check(created.Status == 201, "existing conversation owner creates synthetic interaction");
        var conversation = created.Response!.ConversationId;
        SelectionProposal? accepted = null;
        string? operation = null;
        for (var index = 0; index < 8 && accepted is null; index++)
        {
            var submitted = store.SubmitTurn(device, conversation, Guid.NewGuid(),
                System.Text.Encoding.UTF8.GetBytes("synthetic-turn-" + index),
                new(Guid.NewGuid(), boot, 0, "Mám rád zvláštní příběhy.", 63, 9, PartyWandCatalog.Version));
            Check(submitted.Status == 202, "existing fake turn completed without provider calls");
            operation = submitted.OperationId!;
            accepted = (store.GetOperation(device, operation).Operation?.Result as TurnResult)?.Proposal as SelectionProposal;
        }
        Check(accepted is not null, "existing owner produced a real SelectionProposal");
        var plan = show.ReadFinalePlan(device, operation!)!;
        Check(plan.ProposalId == accepted!.ProposalId && plan.SessionId == accepted.ConversationId
            && plan.SlotId == accepted.Compartment && plan.CatalogVersion == accepted.CatalogVersion
            && plan.SessionGeneration is not null && plan.Profile == "existing-provider"
            && plan.CuePlan.Variant == "no-motor", "common completed-operation path binds original proposal instead of invented ID");
    }

    private static void Check(bool value, string name)
    {
        if (!value) { throw new InvalidOperationException("Show director: " + name); }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => now.UtcTicks;
        internal void Advance(TimeSpan span) => now += span;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "kufr-show-" + Guid.NewGuid().ToString("N"));
        private readonly IConfiguration configuration;
        private readonly OperationJournal journal;
        private readonly EveningMemoryStore evenings;
        internal Clock Clock { get; } = new();
        internal string EveningId { get; }
        internal string DatabasePath => Path.Combine(directory, "journal.sqlite");
        internal EveningMemoryStore Memory => evenings;
        internal Fixture()
        {
            Directory.CreateDirectory(directory);
            configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:DatabasePath"] = Path.Combine(directory, "journal.sqlite"),
                ["Devices:0:DeviceId"] = "show-device", ["Devices:1:DeviceId"] = "other-device",
                ["Keepsake:Origin"] = "https://show.test", ["Keepsake:PublicDeviceId"] = "show-device"
            }).Build();
            journal = new OperationJournal(configuration, Clock);
            evenings = new EveningMemoryStore(configuration, journal, Clock);
            EveningId = evenings.GetStatus("show-device").EveningId;
        }
        internal ShowCoordinator Create() => new(configuration, journal, evenings, Clock);
        internal ConversationStore CreateConversationStore(ShowCoordinator show) => new(Clock, journal,
            eveningMemory: evenings, show: show);
        public void Dispose()
        {
            journal.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
