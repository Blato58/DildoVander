using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Kufr.Backend.Features.Character;
using Kufr.Backend.Features.Conversations;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Show;

// Anonymous direction/projection only. The existing conversation and device keep
// ownership of sessions, selection, playback confirmations and physical outcomes.
internal sealed partial class ShowCoordinator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lock gate = new();
    private readonly string connectionString;
    private readonly TimeProvider clock;
    private readonly EveningMemoryStore evenings;
    private readonly string? publicOrigin;
    private readonly string? publicDeviceId;
    private readonly HashSet<string> devices;
    private readonly Dictionary<string, Runtime> runtimes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> occupations = new(StringComparer.Ordinal);

    internal ShowCoordinator(IConfiguration configuration, OperationJournal journal, EveningMemoryStore evenings,
        TimeProvider clock)
    {
        _ = journal; // Acquire the existing single-process database owner first.
        this.evenings = evenings;
        this.clock = clock;
        var configuredOrigin = configuration["Keepsake:Origin"];
        publicDeviceId = configuration["Keepsake:PublicDeviceId"];
        publicOrigin = Uri.TryCreate(configuredOrigin, UriKind.Absolute, out var origin)
            && origin.Scheme == "https" && origin.UserInfo.Length == 0 && origin.PathAndQuery == "/"
            && origin.Fragment.Length == 0 ? origin.GetLeftPart(UriPartial.Authority) : null;
        devices = configuration.GetSection("Devices").GetChildren().Select(d => d["DeviceId"])
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        devices.Add(Access.ServerAdminAccess.DebugDeviceId);
        var path = configuration["Backend:DatabasePath"] ?? throw new InvalidOperationException("Missing database path.");
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(path), DefaultTimeout = 5 }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS show_director_state(device_id TEXT PRIMARY KEY, body TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS show_director_commands(device_id TEXT NOT NULL, command_id TEXT NOT NULL,
              fingerprint TEXT NOT NULL, expires_at TEXT NOT NULL, created_at TEXT NOT NULL, body TEXT NOT NULL,
              PRIMARY KEY(device_id,command_id));
            """;
        command.ExecuteNonQuery();
        InitializeFinalePlans();
        ShowRepertoire.Validate();
    }

    internal bool IsKnownDevice(string deviceId) => devices.Contains(deviceId);

    internal ShowState Read(string deviceId)
    {
        lock (gate)
        {
            var runtime = Get(deviceId);
            return Snapshot(runtime, runtime.State);
        }
    }

    internal ShowCommandResult Command(string deviceId, ShowCommand command)
    {
        lock (gate)
        {
            var runtime = Get(deviceId);
            var now = clock.GetUtcNow();
            var priorityCancellation = command.Kind is "stop" or "cancel-speech" or "guest-leave";
            var unexpired = runtime.Controls.Where(c => c.ExpiresAt > now).ToArray();
            runtime.Controls.Clear();
            foreach (var pending in unexpired) { runtime.Controls.Enqueue(pending); }
            if (string.IsNullOrWhiteSpace(command.CommandId) || command.CommandId.Length > 128
                || command.ExpectedStateRevision < 0 || command.TtlSeconds is < 1 or > 120
                || command.Value?.Length > 128)
            { return new("rejected", "invalid_command", runtime.State); }
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command, Json))));
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var previous = connection.CreateCommand();
            previous.Transaction = transaction;
            previous.CommandText = "SELECT fingerprint,expires_at,body FROM show_director_commands WHERE device_id=$d AND command_id=$c";
            previous.Parameters.AddWithValue("$d", deviceId);
            previous.Parameters.AddWithValue("$c", command.CommandId);
            using (var reader = previous.ExecuteReader())
            {
                if (reader.Read())
                {
                    if (reader.GetString(0) != fingerprint) { return new("rejected", "idempotency_conflict", runtime.State); }
                    if (DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture) <= now)
                    { return new("expired", "command_expired", runtime.State); }
                    return JsonSerializer.Deserialize<ShowCommandResult>(reader.GetString(2), Json)! with { Status = "replayed" };
                }
            }
            if (command.Kind != "stop" && command.ExpectedStateRevision != runtime.State.Revision)
            { return new("rejected", "stale_revision", runtime.State); }
            using (var count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM show_director_commands WHERE device_id=$d AND created_at >= $cutoff";
                count.Parameters.AddWithValue("$d", deviceId);
                count.Parameters.AddWithValue("$cutoff", now.AddHours(-24).ToString("O"));
                if (!priorityCancellation && Convert.ToInt64(count.ExecuteScalar()) >= 4096)
                { return new("rejected", "command_retention_limit", runtime.State); }
            }
            var sessionCommand = command.Kind is "tone" or "tempo" or "scene-twist" or "quick-finale"
                or "guest-leave" or "cancel-speech" or "mood" or "override";
            if (sessionCommand && (runtime.State.SessionId is null || command.SessionId != runtime.State.SessionId
                || command.SessionGeneration != runtime.State.SessionGeneration))
            { return new("rejected", "stale_session", runtime.State); }
            if (!priorityCancellation && runtime.Controls.Count >= 32) { return new("rejected", "command_queue_full", runtime.State); }
            var state = runtime.State;
            ShowControl? control = null;
            switch (command.Kind)
            {
                case "mood":
                case "mood-evening":
                    if (!MoodReactor.Names.Contains(command.Value)) { return new("rejected", "invalid_value", state); }
                    var mood = MoodReactor.Set(command.Value!, command.Kind == "mood-evening");
                    state = state with { Mood = mood,
                        EveningMood = command.Kind == "mood-evening" ? mood : state.EveningMood ?? state.Mood,
                        MoodScope = command.Kind == "mood-evening" ? "evening" : "session" };
                    if (state.SessionId is not null) { control = new("mood", command.Value, now.AddSeconds(command.TtlSeconds)); }
                    break;
                case "mood-auto": state = state with { Mood = (state.EveningMood ?? state.Mood) with { Locked = false },
                    EveningMood = (state.EveningMood ?? state.Mood) with { Locked = false }, MoodScope = "automatic" }; break;
                case "tone":
                    if (command.Value is not ("reserved" or "dry" or "adult" or "chaos")) { return new("rejected", "invalid_value", state); }
                    state = state with { Tone = command.Value };
                    control = new(command.Kind, command.Value, now.AddSeconds(command.TtlSeconds));
                    break;
                case "tempo":
                    if (command.Value is not ("standard" or "express")) { return new("rejected", "invalid_value", state); }
                    state = state with { Tempo = command.Value };
                    control = new(command.Kind, command.Value, now.AddSeconds(command.TtlSeconds));
                    break;
                case "quiet":
                case "ambient":
                    if (command.Value is not ("on" or "off")) { return new("rejected", "invalid_value", state); }
                    state = command.Kind == "quiet" ? state with { Quiet = command.Value == "on", PendingCue = null }
                        : state with { AmbientEnabled = command.Value == "on", PendingCue = null };
                    break;
                case "ambient-trigger":
                    if (!CanAmbient(runtime)) { return new("rejected", "ambient_busy_or_disabled", state); }
                    state = state with { PendingCue = CreateAmbient(runtime, now, manual: true) };
                    break;
                case "scene-twist":
                    if (!ShowSceneLibrary.Archetypes.Contains(command.Value)) { return new("rejected", "invalid_value", state); }
                    state = state with { SceneId = command.Value };
                    control = new(command.Kind, command.Value, now.AddSeconds(command.TtlSeconds));
                    break;
                case "quick-finale":
                case "guest-leave":
                case "cancel-speech":
                    control = new(command.Kind, null, now.AddSeconds(command.TtlSeconds));
                    state = state with { PendingCue = null, CancellationEpoch = state.CancellationEpoch + 1 };
                    break;
                case "cancel-cue": state = state with { PendingCue = null, CancellationEpoch = state.CancellationEpoch + 1 }; break;
                case "stop":
                    control = state.SessionId is null ? null : new("stop", null, now.AddSeconds(command.TtlSeconds));
                    state = state with { Phase = "stopped", PendingCue = null, AmbientEnabled = false,
                        OccupiedBy = null, CancellationEpoch = state.CancellationEpoch + 1 };
                    break;
                case "recover":
                    if (state.SessionId is not null || state.OccupiedBy is not null) { return new("rejected", "session_active", state); }
                    state = state with { Phase = "idle", PendingCue = null };
                    break;
                case "next-visit":
                    if (state.SessionId is not null || state.OccupiedBy is not null) { return new("rejected", "session_active", state); }
                    if (state.Phase is "stopped" or "interrupted") { return new("rejected", "director_recovery_required", state); }
                    state = state with { Phase = "idle", SceneId = null, PendingCue = null, Tempo = "standard" };
                    break;
                case "override":
                    if (state.SelectionLocked || !int.TryParse(command.Value, out var slot) || slot is < 1 or > 6
                        || (state.ReadyMask & (1 << (slot - 1))) == 0)
                    { return new("rejected", "selection_locked_or_unavailable", state); }
                    control = new(command.Kind, command.Value, now.AddSeconds(command.TtlSeconds));
                    break;
                default: return new("rejected", "invalid_command_kind", state);
            }
            if (command.Kind is "quiet" or "ambient" or "stop")
            {
                state = state with { DeviceSettingsRevision = unchecked(state.DeviceSettingsRevision + 1) };
            }
            if (command.Kind == "stop")
            {
                state = state with { DeviceStopRevision = unchecked(state.DeviceStopRevision + 1) };
            }
            state = Snapshot(runtime, state) with { Revision = state.Revision + 1, UpdatedAt = now };
            var result = new ShowCommandResult("accepted", "direction_accepted", state);
            Save(connection, transaction, state);
            using var receipt = connection.CreateCommand();
            receipt.Transaction = transaction;
            receipt.CommandText = """
                DELETE FROM show_director_commands WHERE created_at < $cutoff;
                INSERT INTO show_director_commands VALUES($d,$c,$f,$e,$n,$b);
                """;
            receipt.Parameters.AddWithValue("$cutoff", now.AddHours(-24).ToString("O"));
            receipt.Parameters.AddWithValue("$d", deviceId);
            receipt.Parameters.AddWithValue("$c", command.CommandId);
            receipt.Parameters.AddWithValue("$f", fingerprint);
            receipt.Parameters.AddWithValue("$e", now.AddSeconds(command.TtlSeconds).ToString("O"));
            receipt.Parameters.AddWithValue("$n", now.ToString("O"));
            receipt.Parameters.AddWithValue("$b", JsonSerializer.Serialize(result, Json));
            receipt.ExecuteNonQuery();
            transaction.Commit();
            runtime.State = state;
            if (priorityCancellation) { runtime.Controls.Clear(); }
            if (priorityCancellation || command.Kind == "cancel-cue")
            {
                runtime.Scene?.Cancel();
                runtime.PendingLoreIds.Clear();
            }
            if (command.Kind == "stop") { occupations.TryRemove(deviceId, out _); }
            if (control is not null) { runtime.Controls.Enqueue(control); }
            return result;
        }
    }

    internal ShowVisit BeginVisit(string deviceId, string? eveningId, string sessionId, string generation)
    {
        lock (gate)
        {
            var runtime = Get(deviceId);
            if (runtime.State.OccupiedBy is not null || runtime.State.SessionId is not null
                || eveningId is not null && eveningId != runtime.State.EveningId
                || eveningId is not null && evenings.ReadChronicle(deviceId, eveningId)?.Provisional == false
                || eveningId is not null && evenings.CaptureShowVisit(deviceId, sessionId) != eveningId
                || runtime.State.Phase is "stopped" or "interrupted")
            { throw new InvalidOperationException("Show director is occupied or requires recovery."); }
            runtime.Controls.Clear();
            runtime.SceneRecorded = false;
            runtime.LastActivity = clock.GetUtcNow();
            var lore = eveningId is null ? new EveningLore(1, runtime.State.EveningId, [])
                : CaptureLore(deviceId, eveningId);
            runtime.Lore = lore;
            runtime.LoreOffered = false;
            runtime.PendingLoreIds.Clear();
            runtime.Scene = null;
            runtime.SceneOwnerGeneration = null;
            runtime.ApprovedFinaleAnchors = [];
            Update(runtime, runtime.State with { EveningId = eveningId ?? runtime.State.EveningId,
                SessionId = sessionId, SessionGeneration = generation, Phase = "greeting", PendingCue = null,
                SessionStartedAt = clock.GetUtcNow(), SceneExecution = null, FinalePlan = null,
                SceneId = ShowSceneLibrary.Select(runtime.RecentScenes.ToArray(), generation),
                CancellationEpoch = runtime.State.CancellationEpoch + 1 });
            return new ShowVisit(this, deviceId, generation, lore);
        }
    }

    internal void ReportSelectionState(string deviceId, string generation, int readyMask, bool locked,
        int? selectedSlot, CharacterTone tone, CharacterTempo tempo)
    {
        lock (gate)
        {
            var runtime = Get(deviceId);
            if (runtime.State.SessionGeneration != generation) { return; }
            var next = runtime.State with { ReadyMask = readyMask, SelectionLocked = locked,
                SelectedSlot = selectedSlot, Tone = tone.ToString().ToLowerInvariant(),
                Tempo = tempo.ToString().ToLowerInvariant() };
            if (next != runtime.State) { Update(runtime, next); }
        }
    }

    internal ShowControl[] TakeControls(string deviceId, string generation)
    {
        lock (gate)
        {
            var runtime = Get(deviceId);
            if (runtime.State.SessionGeneration != generation) { return []; }
            var controls = runtime.Controls.Where(c => c.ExpiresAt > clock.GetUtcNow()).ToArray();
            runtime.Controls.Clear();
            return controls;
        }
    }

    internal string Direction(string deviceId, string generation, CharacterPhase phase, bool express, FinaleProfile profile)
    {
        lock (gate)
        {
            var runtime = Get(deviceId);
            if (runtime.State.SessionGeneration != generation) { return ""; }
            var label = phase.ToString().ToLowerInvariant();
            if (label != runtime.State.Phase)
            {
                Update(runtime, runtime.State with { Phase = label,
                    Mood = MoodReactor.Advance(runtime.State.Mood, clock.GetUtcNow() - runtime.State.UpdatedAt) });
            }
            var mood = $"Nálada kufru={runtime.State.Mood.Dominant}; je to fikce, ne emoce hosta.";
            var loreDirection = "";
            if (!runtime.LoreOffered && phase is not (CharacterPhase.Idle or CharacterPhase.Listening or CharacterPhase.Stopped)
                && runtime.Lore?.Motifs.FirstOrDefault() is { } motif)
            {
                runtime.LoreOffered = true;
                runtime.PendingLoreIds.Add(motif.Id);
                loreDirection = " Večerní anonymní fikce: " + motif.Text;
            }
            if (phase is not (CharacterPhase.Ready or CharacterPhase.Closing)) { return mood + loreDirection; }
            if (runtime.Scene is { } execution) { return mood + " " + execution.ActorDirection + loreDirection; }
            var scene = ShowSceneLibrary.Create(runtime.State.SceneId ?? ShowSceneLibrary.Archetypes[0], generation,
                express, false, profile);
            return mood + " " + scene.Cues.First(c => c.Kind == "ActorDirection").Value + loreDirection;
        }
    }

    internal void EndVisit(string deviceId, string generation, bool completed = false, bool endedNormally = false)
    {
        lock (gate)
        {
            var runtime = Get(deviceId);
            if (runtime.State.SessionGeneration != generation) { return; }
            runtime.Controls.Clear();
            runtime.LastActivity = clock.GetUtcNow();
            RecordVisitEnd(runtime.State, completed);
            runtime.PendingLoreIds.Clear();
            var execution = completed && runtime.State.Phase != "stopped"
                ? runtime.Scene?.RetireOptional() : runtime.Scene?.Cancel();
            var mood = MoodReactor.Advance(runtime.State.EveningMood ?? runtime.State.Mood,
                clock.GetUtcNow() - runtime.State.UpdatedAt, completed: completed && runtime.State.Phase != "stopped");
            Update(runtime, runtime.State with { SessionId = null, SessionGeneration = null,
                Phase = runtime.State.Phase == "stopped" ? "stopped" : completed || endedNormally ? "idle" : "interrupted",
                AmbientEnabled = runtime.State.AmbientEnabled && (completed || endedNormally), PendingCue = null,
                SessionStartedAt = null, Mood = mood, EveningMood = mood, MoodScope = mood.Locked ? "evening" : "automatic",
                SceneExecution = execution });
        }
    }

    internal ShowSceneProgress? BeginFinale(string deviceId, string generation, bool express,
        FinaleProfile profile, bool selectionValid, long decisionRevision, int? selectedSlot = null)
    {
        lock (gate)
        {
            var runtime = Get(deviceId);
            if (runtime.State.SessionGeneration != generation) { return null; }
            runtime.Scene?.Cancel();
            var definition = ShowSceneLibrary.Create(runtime.State.SceneId ?? ShowSceneLibrary.Archetypes[0],
                generation, express, selectionValid && profile == FinaleProfile.Limited, profile);
            definition = definition with { Cues = definition.Cues.Select(cue => cue with
                { TargetSlot = selectionValid && selectedSlot is >= 1 and <= 6 ? selectedSlot : null }).ToArray() };
            runtime.Scene = new ShowSceneExecution(definition, clock, selectionValid,
                readSceneCueStatus is null ? null : cue => readSceneCueStatus(deviceId, generation, cue.CueId));
            runtime.SceneDecisionRevision = decisionRevision;
            runtime.SceneOwnerGeneration = generation;
            runtime.FinaleSources[generation] = new(runtime.State.SessionId!, runtime.State.EveningId,
                definition.SceneId, express, profile, decisionRevision, runtime.ApprovedFinaleAnchors.ToArray());
            while (runtime.FinaleSources.Count > 4) { runtime.FinaleSources.Remove(runtime.FinaleSources.Keys.First()); }
            return ProjectScene(runtime, runtime.Scene.Tick());
        }
    }

    internal ShowSceneProgress? OnTerminalAudioStarted(string deviceId, string generation, long decisionRevision)
    {
        lock (gate)
        {
            var runtime = Get(deviceId);
            return runtime.State.SessionGeneration == generation && runtime.SceneDecisionRevision == decisionRevision
                && runtime.Scene is { } scene
                ? ProjectScene(runtime, scene.TerminalAudioStarted()) : null;
        }
    }

    internal ShowSceneProgress? OnPlaybackConfirmed(string deviceId, string generation, long decisionRevision)
    {
        lock (gate)
        {
            var runtime = Get(deviceId);
            return runtime.State.SessionGeneration == generation && runtime.SceneDecisionRevision == decisionRevision
                && runtime.Scene is { } scene
                ? ProjectScene(runtime, scene.PlaybackConfirmed()) : null;
        }
    }

    internal ShowSceneProgress? TickScene(string deviceId, string generation)
    {
        lock (gate)
        {
            var runtime = Get(deviceId);
            return runtime.State.SessionGeneration == generation && runtime.Scene is { } scene
                ? ProjectScene(runtime, scene.Tick()) : null;
        }
    }

    internal void CancelScene(string deviceId, string generation)
    {
        lock (gate)
        {
            var runtime = Get(deviceId);
            if (runtime.State.SessionGeneration != generation) { return; }
            runtime.PendingLoreIds.Clear();
            runtime.FinaleSources.Remove(generation);
            runtime.ApprovedFinaleAnchors = [];
            if (runtime.Scene is { } scene) { ProjectScene(runtime, scene.Cancel()); }
            Update(runtime, runtime.State with { PendingCue = null, CancellationEpoch = runtime.State.CancellationEpoch + 1 });
        }
    }

    private ShowSceneProgress ProjectScene(Runtime runtime, ShowSceneProgress progress)
    {
        if (runtime.State.SceneExecution?.Revision != progress.Revision
            || runtime.State.SceneExecution?.SceneId != progress.SceneId)
        {
            Update(runtime, runtime.State with { SceneExecution = progress,
                PendingCue = progress.Completed || progress.Cancelled ? null : progress.LastUiBeat });
        }
        return progress;
    }

    internal void ConfirmSpeechProxy(string deviceId, string generation)
    {
        lock (gate)
        {
            var runtime = Get(deviceId);
            if (runtime.State.SessionGeneration != generation || runtime.State.SessionId is not { } session
                || runtime.Lore is not { } lore) { return; }
            // The existing audio sender supplies this observed submission proxy.
            // It never claims exact model wording or physical playback completion.
            foreach (var id in runtime.PendingLoreIds)
            { evenings.RecordEveningJokeUse(deviceId, lore.EveningId, session, id); }
            runtime.PendingLoreIds.Clear();
        }
    }

    private EveningLore CaptureLore(string deviceId, string eveningId)
    {
        var source = evenings.BuildEveningLore(deviceId, eveningId);
        var motifs = source.Motifs.Take(3).Select(motif =>
        {
            var text = motif.Text;
            // Limited/Blocked speech cannot accidentally disclose a catalog name
            // through otherwise authorized aggregate lore.
            foreach (var wand in PartyWandCatalog.Available(63))
            { text = text.Replace(wand.DisplayName, "Jedna katalogová rekvizita", StringComparison.Ordinal); }
            return motif with { Text = text };
        }).ToArray();
        return source with { Motifs = motifs };
    }

    private void RecordVisitEnd(ShowState state, bool completed)
    {
        if (state.SessionId is not { } session || state.EveningId == "no-active-evening") { return; }
        var captured = evenings.ReadCapturedShowEvening(state.DeviceId, session);
        if (captured != state.EveningId) { return; }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state.DeviceId + "\n" + session)));
        evenings.RecordShowEvent(new("visit-end:" + hash,
            completed ? ShowMemoryEventKind.VisitCompleted : ShowMemoryEventKind.VisitInterrupted,
            state.DeviceId, captured, session, 1, "show_director_dialogue", session));
    }

    // Recorded only when the existing audio sender observed a submitted speech
    // proxy; this is variety history, never proof of the exact generated words.
    internal void RecordSceneSpeechProxy(string deviceId, string generation)
    {
        lock (gate)
        {
            var runtime = Get(deviceId);
            if (runtime.State.SessionGeneration != generation || runtime.SceneRecorded || runtime.State.SceneId is not { } scene) { return; }
            runtime.SceneRecorded = true;
            runtime.RecentScenes.Enqueue(scene);
            while (runtime.RecentScenes.Count > 12) { runtime.RecentScenes.Dequeue(); }
            Update(runtime, runtime.State);
        }
    }

    internal bool TryCloseEvening(string deviceId, long expectedRevision, string eveningId, Action close)
    {
        lock (gate)
        {
            var runtime = Get(deviceId);
            if (runtime.State.Revision != expectedRevision || runtime.State.EveningId != eveningId
                || runtime.State.SessionId is not null || runtime.State.OccupiedBy is not null) { return false; }
            close();
            runtime.Controls.Clear();
            Update(runtime, runtime.State with { PendingCue = null, AmbientEnabled = false,
                CancellationEpoch = runtime.State.CancellationEpoch + 1 });
            return true;
        }
    }

    internal bool TryOccupy(string deviceId, string eveningId, string occupantId)
    {
        lock (gate)
        {
            var runtime = Get(deviceId);
            if (eveningId == "no-active-evening" || runtime.State.EveningId != eveningId || runtime.State.SessionId is not null || runtime.State.OccupiedBy is not null
                || evenings.ReadChronicle(deviceId, eveningId)?.Provisional == false
                || runtime.State.Phase is "stopped" or "interrupted" || runtime.State.Quiet) { return false; }
            Update(runtime, runtime.State with { OccupiedBy = occupantId, PendingCue = null, Phase = "duel" });
            occupations[deviceId] = occupantId;
            return true;
        }
    }

    internal bool IsOccupationCurrent(string deviceId, string occupantId)
    {
        return occupations.TryGetValue(deviceId, out var current) && current == occupantId;
    }

    internal void Release(string deviceId, string occupantId)
    {
        lock (gate)
        {
            var runtime = Get(deviceId);
            if (runtime.State.OccupiedBy != occupantId) { return; }
            runtime.LastActivity = clock.GetUtcNow();
            Update(runtime, runtime.State with { OccupiedBy = null, Phase = "idle" });
            occupations.TryRemove(deviceId, out _);
        }
    }

    internal bool PublishDuelBeat(string deviceId, string duelId, int round, string quip)
    {
        lock (gate)
        {
            var runtime = Get(deviceId);
            if (round is < 1 or > 3 || string.IsNullOrWhiteSpace(quip) || quip.Length > 400
                || runtime.State.OccupiedBy != duelId) { return false; }
            var cue = new ShowCue($"{duelId}/round/{round}", "UiBeat", "duel_round_completed", 0, 8000,
                "drop", duelId, quip);
            if (runtime.State.PendingCue?.CueId == cue.CueId) { return true; }
            Update(runtime, runtime.State with { PendingCue = cue });
            return true;
        }
    }

    internal ShowCue? TickAmbient(string deviceId, bool runtimeIdle, bool audioAvailable)
    {
        lock (gate)
        {
            var runtime = Get(deviceId);
            if (!runtimeIdle || !CanAmbient(runtime))
            {
                if (runtime.State.PendingCue is not null) { Update(runtime, runtime.State with { PendingCue = null }); }
                return null;
            }
            if (!audioAvailable && runtime.State.PendingCue?.Kind == "ActorDirection")
            { Update(runtime, runtime.State with { PendingCue = null }); }
            var now = clock.GetUtcNow();
            if (runtime.State.PendingCue is not null && now - runtime.State.UpdatedAt < TimeSpan.FromSeconds(8)) { return null; }
            var idle = now - runtime.LastActivity;
            if (idle < TimeSpan.FromSeconds(45)) { return null; }
            var cue = CreateAmbient(runtime, now, false, audioAvailable);
            if (cue is null) { return null; }
            Update(runtime, runtime.State with { PendingCue = cue });
            return cue;
        }
    }

    internal void ConfirmAmbientPlayback(string deviceId, string cueId)
    {
        lock (gate)
        {
            var runtime = Get(deviceId);
            if (runtime.State.PendingCue is not { Kind: "ActorDirection" } cue || cue.CueId != cueId || cue.SessionGeneration != "idle"
                || clock.GetUtcNow() - runtime.State.UpdatedAt >= TimeSpan.FromMilliseconds(cue.DeadlineMs)) { return; }
            runtime.PlayedLines.Enqueue(cue.Value);
            while (runtime.PlayedLines.Count > 12) { runtime.PlayedLines.Dequeue(); }
            Update(runtime, runtime.State with { PendingCue = null });
        }
    }

    private bool CanAmbient(Runtime runtime) => runtime.State.AmbientEnabled && !runtime.State.Quiet
        && runtime.State.SessionId is null && runtime.State.OccupiedBy is null && runtime.State.Phase == "idle";

    private ShowCue? CreateAmbient(Runtime runtime, DateTimeOffset now, bool manual, bool audioAvailable = true)
    {
        while (runtime.Spoken.Count > 0 && now - runtime.Spoken.Peek() >= TimeSpan.FromMinutes(10)) { runtime.Spoken.Dequeue(); }
        var speech = audioAvailable && now - runtime.LastActivity >= TimeSpan.FromSeconds(120)
            && now - runtime.LastSpeech >= TimeSpan.FromSeconds(180) && runtime.Spoken.Count < 3;
        if (!speech && !manual && now - runtime.LastLight < TimeSpan.FromSeconds(45)) { return null; }
        var id = Guid.NewGuid().ToString("N");
        if (!speech)
        {
            runtime.LastLight = now;
            return new(id, "UiBeat", "idle", 0, 8000, "drop", "idle", "ambient-attention-no-motor");
        }
        var line = ShowRepertoire.Ambient.FirstOrDefault(l => !runtime.ScheduledLines.TakeLast(4).Contains(l.Id)
                && !runtime.PlayedLines.TakeLast(4).Contains(l.Id))
            ?? ShowRepertoire.Ambient[(int)(runtime.AmbientSequence % ShowRepertoire.Ambient.Length)];
        runtime.ScheduledLines.Enqueue(line.Id);
        while (runtime.ScheduledLines.Count > 12) { runtime.ScheduledLines.Dequeue(); }
        runtime.AmbientSequence++;
        // Reserve at scheduling time too: lost completion events cannot increase speech rate.
        runtime.Spoken.Enqueue(now);
        runtime.LastSpeech = now;
        return new(id, "ActorDirection", "idle", 0, 8000, "drop", "idle", line.Id);
    }

    private Runtime Get(string deviceId)
    {
        if (!IsKnownDevice(deviceId)) { throw new ArgumentException("Unknown device."); }
        // An active owner already captured its evening. Avoid synchronous memory
        // reads on every audio/cue tick; refresh the active evening only when idle.
        if (runtimes.TryGetValue(deviceId, out var active)
            && (active.State.SessionId is not null || active.State.OccupiedBy is not null)) { return active; }
        var evening = evenings.ReadActiveEveningId(deviceId) ?? "no-active-evening";
        if (!runtimes.TryGetValue(deviceId, out var runtime))
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT body FROM show_director_state WHERE device_id=$d";
            command.Parameters.AddWithValue("$d", deviceId);
            var saved = command.ExecuteScalar() as string;
            var state = saved is null ? new ShowState(deviceId, evening, 0, null, null, "idle", new(),
                "adult", "standard", false, false, null, null, null, clock.GetUtcNow())
                : JsonSerializer.Deserialize<ShowState>(saved, Json)!;
            state = state with { FinalePlan = ReadLatestFinalePlan(deviceId) };
            runtime = new Runtime(state, clock.GetUtcNow());
            runtimes.Add(deviceId, runtime);
            if (state.SessionId is not null || state.OccupiedBy is not null)
            {
                RecordVisitEnd(state, completed: false);
                Update(runtime, state with { SessionId = null, SessionGeneration = null, OccupiedBy = null,
                    SessionStartedAt = null, Phase = "interrupted", AmbientEnabled = false, PendingCue = null,
                    CancellationEpoch = state.CancellationEpoch + 1 });
            }
        }
        if (runtime.State.EveningId != evening && runtime.State.SessionId is null && runtime.State.OccupiedBy is null)
        {
            runtime.Controls.Clear();
            runtime.RecentScenes.Clear();
            runtime.PlayedLines.Clear();
            runtime.ScheduledLines.Clear();
            runtime.Spoken.Clear();
            runtime.LastSpeech = DateTimeOffset.MinValue;
            Update(runtime, runtime.State with { EveningId = evening, Mood = new(), EveningMood = new(),
                MoodScope = "automatic", PendingCue = null });
        }
        return runtime;
    }

    private void Update(Runtime runtime, ShowState state)
    {
        state = Snapshot(runtime, state) with { Revision = runtime.State.Revision + 1, UpdatedAt = clock.GetUtcNow() };
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Save(connection, transaction, state);
        transaction.Commit();
        runtime.State = state;
    }

    private ShowState Snapshot(Runtime runtime, ShowState state) => state with
    {
        PublicOrigin = state.DeviceId == publicDeviceId ? publicOrigin : null,
        RecentWandLineIds = runtime.WandLines.ToArray(),
        ScheduledWandLineIds = runtime.ScheduledWandLines.ToArray(),
        ScheduledWandCounters = new(runtime.WandCounters, StringComparer.Ordinal),
        RecentSceneIds = runtime.RecentScenes.ToArray(), RecentAmbientIds = runtime.PlayedLines.ToArray(),
        ScheduledAmbientIds = runtime.ScheduledLines.ToArray(), AmbientSpeechWindow = runtime.Spoken.ToArray(),
        LastAmbientSpeechAt = runtime.LastSpeech
    };

    private static void Save(SqliteConnection connection, SqliteTransaction transaction, ShowState state)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO show_director_state VALUES($d,$b) ON CONFLICT(device_id) DO UPDATE SET body=$b";
        command.Parameters.AddWithValue("$d", state.DeviceId);
        command.Parameters.AddWithValue("$b", JsonSerializer.Serialize(state, Json));
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private sealed class Runtime(ShowState state, DateTimeOffset now)
    {
        internal ShowState State = state;
        internal readonly Queue<ShowControl> Controls = new();
        internal readonly Queue<string> RecentScenes = new(state.RecentSceneIds ?? []);
        internal readonly Queue<string> WandLines = new(state.RecentWandLineIds ?? []);
        internal readonly Queue<string> ScheduledWandLines = new(state.ScheduledWandLineIds ?? []);
        internal readonly Dictionary<string, int> WandCounters = new(state.ScheduledWandCounters ?? [], StringComparer.Ordinal);
        internal string[] PendingWandLines = [];
        internal int? PendingWandSlot;
        internal long PendingWandRevision;
        internal readonly Queue<string> PlayedLines = new(state.RecentAmbientIds ?? []);
        internal readonly Queue<string> ScheduledLines = new(state.ScheduledAmbientIds ?? []);
        internal readonly Queue<DateTimeOffset> Spoken = new(state.AmbientSpeechWindow ?? []);
        internal DateTimeOffset LastActivity = now;
        internal DateTimeOffset LastSpeech = state.LastAmbientSpeechAt ?? DateTimeOffset.MinValue;
        internal DateTimeOffset LastLight = DateTimeOffset.MinValue;
        internal long AmbientSequence;
        internal bool SceneRecorded;
        internal EveningLore? Lore;
        internal bool LoreOffered;
        internal readonly List<string> PendingLoreIds = [];
        internal ShowSceneExecution? Scene;
        internal string? SceneOwnerGeneration;
        internal long SceneDecisionRevision;
        internal readonly Dictionary<string, FinaleSource> FinaleSources = new(StringComparer.Ordinal);
        internal FinaleAnchorReference[] ApprovedFinaleAnchors = [];
    }
}

internal sealed class ShowVisit(ShowCoordinator owner, string deviceId, string generation, EveningLore lore) : IDisposable
{
    internal EveningLore Lore { get; } = lore;
    private bool completed;
    private bool endedNormally;
    internal void Complete() => completed = true;
    internal void Leave() => endedNormally = true;
    public void Dispose() => owner.EndVisit(deviceId, generation, completed, endedNormally);
}
