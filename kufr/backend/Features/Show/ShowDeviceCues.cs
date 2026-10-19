using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Speech;
using Kufr.Backend.Features.VoiceTurns;

namespace Kufr.Backend.Features.Show;

// A bounded, restart-empty delivery mailbox. It owns no session or actuator.
// Audio claims are created by accepted show intents/background ticks, never GET.
internal sealed partial class ShowDeviceCueBridge
{
    internal const int MaximumWavBytes = 44 + 32000 * 2 * 8;
    private readonly object gate = new();
    private readonly ShowCoordinator show;
    private readonly OperationJournal journal;
    private readonly VoiceProviderRunner voice;
    private readonly TimeProvider clock;
    private readonly SpeechOptions speech;
    private readonly string speechMode;
    private readonly CancellationToken stopping;
    private readonly Dictionary<string, Mailbox> mailboxes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> narration = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Presence> presence = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> sceneStatuses = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim audioSlot = new(1, 1);

    public ShowDeviceCueBridge(IConfiguration configuration, ShowCoordinator show, OperationJournal journal,
        VoiceProviderRunner voice, TimeProvider clock, IHostApplicationLifetime? host = null)
    {
        this.show = show;
        this.journal = journal;
        this.voice = voice;
        this.clock = clock;
        stopping = host?.ApplicationStopping ?? voice.Stopping;
        speechMode = configuration["Show:DeviceCueSpeechMode"] ?? "configured";
        if (speechMode is not ("configured" or "fake" or "off"))
        { throw new InvalidOperationException("Unsupported Show:DeviceCueSpeechMode."); }
        speech = SpeechOptions.Resolve(configuration["Backend:Voice:Voice"],
            configuration.GetValue<double?>("Backend:Voice:Speed"));
        if (!SpeechOptions.IsValid(speech.Voice, speech.Speed))
        { throw new InvalidOperationException("Invalid configured device cue voice."); }
        show.ConfigureSceneCueAdapter(EnqueueSceneCue, ReadSceneCueStatus);
    }

    private bool SpeechAvailable => speechMode == "fake" || speechMode == "configured" && voice.Enabled;
    private static string Key(string device, string duel) => device + "\n" + duel;

    internal bool HasRecentDevicePoll(string deviceId) => presence.TryGetValue(deviceId, out var seen)
        && clock.GetElapsedTime(seen.At) <= TimeSpan.FromSeconds(10) && seen.SettingsKnown;

    internal string? DeviceNarrationUnavailableReason(string deviceId)
    {
        if (!presence.TryGetValue(deviceId, out var seen) || clock.GetElapsedTime(seen.At) > TimeSpan.FromSeconds(10))
        { return "device_not_connected"; }
        if (!seen.SettingsKnown) { return "device_settings_unconfirmed"; }
        if (seen.Quiet) { return "device_quiet"; }
        if (speechMode == "fake") { return "synthetic_speech_only"; }
        return SpeechAvailable ? null : "speech_unavailable";
    }

    internal string DuelNarrationState(string deviceId, string duelId) => narration.GetValueOrDefault(Key(deviceId, duelId), "idle");

    internal bool EnqueueDuelLine(string deviceId, string duelId, int round, string sceneId,
        int compartment, string speechText)
    {
        if (!Guid.TryParseExact(duelId, "D", out var duel) || duel == Guid.Empty || round is < 0 or > 3
            || sceneId is not ("wake" or "consider" or "farewell") || compartment is < 1 or > 6
            || string.IsNullOrWhiteSpace(speechText) || Encoding.UTF8.GetByteCount(speechText) > 500
            || !SpeechAvailable || !show.IsKnownDevice(deviceId)) { return false; }
        lock (gate)
        {
            var state = show.Read(deviceId);
            var box = Get(deviceId);
            Prune(deviceId, box, state);
            var identity = duelId + "/" + sceneId + "/" + round;
            if (box.Known.TryGetValue(identity, out var previous))
            { return previous.Text == speechText && previous.Compartment == compartment && previous.Status is not ("failed" or "cancelled"); }
            if (box.Poll is null || !HasRecentDevicePoll(deviceId) || state.Quiet || box.Poll.AppliedQuiet == true
                || state.SessionId is not null || !show.IsOccupationCurrent(deviceId, duelId)
                || DuelNarrationState(deviceId, duelId) == "failed" || box.Queue.Count >= 4) { return false; }
            var entry = new Entry(Guid.NewGuid(), duel, round, sceneId, compartment, speechText,
                state.CancellationEpoch, box.Poll, clock.GetTimestamp(), identity);
            entry.Speech = speech with { Voice = box.Poll.Voice ?? speech.Voice };
            box.Queue.Enqueue(entry);
            box.Known.Add(identity, entry);
            box.History.Add(entry.Id, entry);
            TrimHistory(deviceId, box);
            narration[Key(deviceId, duelId)] = "pending";
            StartAudio(deviceId, box, entry, state);
            return true;
        }
    }

    internal ShowDeviceCueEnvelope Poll(string deviceId, ShowDeviceCuePoll request)
    {
        if (!ValidPoll(request)) { throw new ArgumentException("Invalid device cue poll."); }
        lock (gate)
        {
            var box = Get(deviceId);
            if (box.Poll is { } old && (old.BootId != request.BootId || old.Generation != request.Generation
                || old.DeviceRevision != request.DeviceRevision || request.DeviceTimeMs < old.DeviceTimeMs
                || old.Voice != request.Voice))
            { CancelBox(deviceId, box, "device_snapshot_changed"); }
            box.Poll = request;
            show.ReportDeviceCueSettings(deviceId, request);
            var state = show.Read(deviceId);
            var known = request.AppliedSettingsRevision == state.DeviceSettingsRevision
                && request.AppliedQuiet is not null && request.AppliedAmbient is not null;
            presence[deviceId] = new(clock.GetTimestamp(), known, state.Quiet || request.AppliedQuiet == true);
            Prune(deviceId, box, state);
            var entry = box.Queue.TryPeek(out var pending) ? pending : null;
            if (entry is not null && entry.Descriptor is null)
            {
                entry.PolledAt = clock.GetTimestamp();
                entry.Descriptor = new(entry.Id, entry.Duel, entry.Round, request.BootId, request.Generation,
                    request.DeviceRevision, request.DeviceTimeMs, request.DeviceTimeMs + 2000, entry.Scene,
                    entry.Compartment, entry.Text, entry.Text.Length == 0 ? null : $"/v1/show/device-cues/{entry.Id:D}/audio",
                    entry.ClipId, entry.CueType, entry.Preset, entry.Speech.Voice);
            }
            return new(state.Quiet, state.AmbientEnabled, state.DeviceSettingsRevision, entry?.Descriptor,
                box.CancelledCue, state.DeviceStopRevision);
        }
    }

    internal ShowDeviceCueAckResult Acknowledge(string deviceId, Guid cueId, ShowDeviceCueAck request)
    {
        if (request.BootId == Guid.Empty || request.Generation < 0 || request.DeviceRevision < 0
            || request.Outcome is not ("accepted" or "completed" or "rejected" or "cancelled")
            || request.Evidence is not ("commanded" or "playback_finished" or "missing_assets")
            || request.Reason?.Length > 128) { return new(400, "invalid_cue_ack"); }
        lock (gate)
        {
            var box = Get(deviceId);
            var state = show.Read(deviceId);
            Prune(deviceId, box, state);
            if (!box.History.TryGetValue(cueId, out var entry) || entry.Descriptor is null
                || !Matches(entry, request.BootId, request.Generation, request.DeviceRevision)
                || !Current(deviceId, box, entry, state)) { return new(409, "stale_cue"); }
            var fingerprint = JsonSerializer.Serialize(request);
            if (entry.Acks.TryGetValue(request.Outcome, out var previous))
            { return previous == fingerprint ? new(200, "ack_replayed", request.Outcome) : new(409, "ack_conflict"); }
            if (entry.Status is "completed" or "failed" or "cancelled") { return new(409, "cue_terminal"); }
            switch (request.Outcome)
            {
                case "accepted":
                    if (request.Evidence != "commanded") { return new(400, "invalid_cue_evidence"); }
                    entry.Status = "accepted";
                    entry.AcceptedAt = clock.GetTimestamp();
                    UpdateSceneStatus(deviceId, entry);
                    break;
                case "completed":
                    var playback = entry.Text.Length > 0 || entry.CueType == "PlayLocalClip";
                    if (entry.Status != "accepted" || playback && request.Evidence != "playback_finished"
                        || entry.Text.Length > 0 && entry.Audio is null || !playback && request.Evidence != "commanded")
                    { return new(409, "playback_not_confirmed"); }
                    entry.Status = "completed";
                    if (entry.AmbientSource is { } ambient) { show.ConfirmDeviceAmbientCue(deviceId, ambient, entry.Text.Length > 0); }
                    Finish(deviceId, box, entry, failed: false);
                    break;
                case "rejected":
                case "cancelled":
                    if (request.Evidence == "playback_finished") { return new(400, "invalid_cue_evidence"); }
                    entry.Status = request.Outcome == "cancelled" ? "cancelled" : "failed";
                    Finish(deviceId, box, entry, failed: true);
                    break;
            }
            entry.Acks.Add(request.Outcome, fingerprint);
            return new(200, "ack_recorded", request.Outcome);
        }
    }

    // Read-only: no claim, provider call or retry can originate from GET audio.
    internal ShowDeviceCueAudioResult ReadAudio(string deviceId, Guid cueId)
    {
        lock (gate)
        {
            var box = Get(deviceId);
            var state = show.Read(deviceId);
            Prune(deviceId, box, state);
            if (!box.History.TryGetValue(cueId, out var entry) || entry.Status != "accepted"
                || !Current(deviceId, box, entry, state)) { return new(409, "stale_cue"); }
            if (entry.Audio is { } audio) { return new(200, "audio_ready", audio.ToArray()); }
            return entry.AudioFailed ? new(503, "cue_audio_unavailable") : new(202, "audio_pending");
        }
    }

    // Runs from the existing show intent and a bounded background worker, never
    // a public content read or a device poll. Camera presence is irrelevant.
    internal void Tick()
    {
        lock (gate)
        {
            foreach (var (device, box) in mailboxes)
            {
                var state = show.Read(device);
                Prune(device, box, state);
                if (box.Queue.Count == 0 && HasRecentDevicePoll(device) && box.Poll is { AppliedQuiet: false, AppliedAmbient: true } poll
                    && poll.AppliedSettingsRevision == state.DeviceSettingsRevision && !state.Quiet && state.AmbientEnabled
                    && state.SessionId is null && state.OccupiedBy is null && state.Phase == "idle")
                {
                    var existing = state.PendingCue is { Binding: "idle" } pending
                        && clock.GetUtcNow() - state.UpdatedAt < TimeSpan.FromMilliseconds(pending.DeadlineMs)
                        && (pending.Kind != "ActorDirection" || SpeechAvailable) ? pending : null;
                    var alreadyScheduled = existing is not null && box.History.Values.Any(item => item.AmbientSource == existing.CueId);
                    var source = alreadyScheduled ? null : existing
                        ?? show.TickAmbient(device, runtimeIdle: true, audioAvailable: SpeechAvailable);
                    if (source is not null)
                    {
                        var text = source.Kind == "ActorDirection"
                            ? ShowRepertoire.Ambient.Single(line => line.Id == source.Value).Text : "";
                        var ambientEntry = new Entry(Guid.NewGuid(), null, 0, "ambient", 1, text,
                            state.CancellationEpoch, poll, clock.GetTimestamp(), "ambient/" + source.CueId)
                        { AmbientSource = source.CueId, Speech = speech with { Voice = poll.Voice ?? speech.Voice } };
                        box.Queue.Enqueue(ambientEntry);
                        box.History.Add(ambientEntry.Id, ambientEntry);
                        TrimHistory(device, box);
                    }
                }
                if (box.Queue.TryPeek(out var entry)) { StartAudio(device, box, entry, show.Read(device)); }
            }
        }
    }

    internal Task AudioWork(string deviceId, Guid cueId)
    {
        lock (gate) { return Get(deviceId).History.TryGetValue(cueId, out var entry) ? entry.Work ?? Task.CompletedTask : Task.CompletedTask; }
    }

    internal Task DrainAudioWork()
    {
        lock (gate) { return Task.WhenAll(mailboxes.Values.SelectMany(box => box.History.Values)
            .Select(entry => entry.Work).OfType<Task>()); }
    }

    internal void CancelAll()
    {
        lock (gate) { foreach (var (device, box) in mailboxes) { CancelBox(device, box, "host_stopping"); } }
    }

    private void StartAudio(string device, Mailbox box, Entry entry, ShowState state)
    {
        if (entry.Text.Length == 0 || entry.Claimed || entry.AudioFailed || Terminal(entry)
            || !box.Queue.TryPeek(out var head) || !ReferenceEquals(head, entry) || !Current(device, box, entry, state)
            || !SpeechAvailable || !audioSlot.Wait(0)) { return; }
        var acquiredVoice = speechMode != "fake" && voice.TryAcquire();
        if (speechMode != "fake" && !acquiredVoice) { audioSlot.Release(); return; }
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Text + "\n" + entry.Speech.Voice
                + "\n" + entry.Speech.Speed.ToString(CultureInfo.InvariantCulture))));
            var reservation = journal.Reserve(device, $"/v1/show/device-cues/{entry.Id:D}/audio", entry.Id, hash,
                new(device, entry.Snapshot.BootId, entry.Duel?.ToString("D") ?? entry.Id.ToString("D"),
                    entry.Id, entry.Snapshot.Generation, entry.Snapshot.DeviceRevision),
                kind: "speech", consumesTurn: false,
                estimatedCostMicroUsd: speechMode == "fake" ? 0 : VoiceProviderRunner.SpeechReservationMicroUsd);
            entry.Claimed = true;
            if (reservation.Existing || reservation.Conflict || reservation.OperationId.Length == 0)
            { throw new InvalidOperationException("Speech claim was already consumed or rejected."); }
            entry.OperationId = reservation.OperationId;
            if (!journal.MarkRunning(reservation.OperationId))
            { throw new InvalidOperationException("Speech claim is not dispatchable."); }
            entry.Work = Task.Run(() => GenerateAudioAsync(device, box, entry, acquiredVoice));
        }
        catch
        {
            entry.Claimed = true;
            entry.AudioFailed = true;
            if (acquiredVoice) { voice.Release(); }
            audioSlot.Release();
        }
    }

    private async Task GenerateAudioAsync(string device, Mailbox box, Entry entry, bool acquiredVoice)
    {
        byte[]? wav = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(entry.Lifetime.Token, stopping);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            wav = speechMode == "fake" ? PcmSpeechAudio.ToDeviceWav(new byte[4800])
                : await voice.SpeakAsync(entry.Text, entry.Speech, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (!ValidWav(wav)) { throw new InvalidDataException("Invalid bounded device cue audio."); }
            lock (gate)
            {
                if (Terminal(entry) || entry.Lifetime.IsCancellationRequested || !Current(device, box, entry, show.Read(device)))
                { throw new OperationCanceledException(); }
                journal.MarkCompleted(entry.OperationId!);
                entry.Audio = wav;
                wav = null;
            }
        }
        catch
        {
            lock (gate)
            {
                entry.AudioFailed = true;
                if (entry.OperationId is { } operation)
                {
                    try { journal.MarkFailed(operation); }
                    catch (Exception error) when (error is IOException or Microsoft.Data.Sqlite.SqliteException)
                    { /* The conservative usage reservation remains; no provider retry. */ }
                }
            }
        }
        finally
        {
            if (wav is not null) { CryptographicOperations.ZeroMemory(wav); }
            if (acquiredVoice) { voice.Release(); }
            audioSlot.Release();
        }
    }

    private bool Current(string device, Mailbox box, Entry entry, ShowState state) => !stopping.IsCancellationRequested
        && state.CancellationEpoch == entry.Epoch && !state.Quiet
        && state.Phase is not ("stopped" or "interrupted") && box.Poll is { } poll && poll.AppliedQuiet != true
        && Matches(entry, poll.BootId, poll.Generation, poll.DeviceRevision)
        && (entry.SceneGeneration is { } generation ? state.SessionGeneration == generation && state.OccupiedBy is null
            : entry.Duel is { } duel ? state.SessionId is null && show.IsOccupationCurrent(device, duel.ToString("D"))
            : state.SessionId is null && state.OccupiedBy is null && state.AmbientEnabled && poll.AppliedAmbient == true);

    private void Prune(string device, Mailbox box, ShowState state)
    {
        foreach (var entry in box.Queue)
        {
            if (Terminal(entry)) { continue; }
            var expired = entry.AcceptedAt is { } accepted ? clock.GetElapsedTime(accepted) >= TimeSpan.FromSeconds(20)
                : entry.PolledAt is { } polled ? clock.GetElapsedTime(polled) >= TimeSpan.FromSeconds(2)
                : clock.GetElapsedTime(entry.CreatedAt) >= TimeSpan.FromSeconds(20);
            if (!Current(device, box, entry, state) || expired || entry.AudioFailed)
            {
                entry.Status = "failed";
                box.CancelledCue = entry.Descriptor is null ? box.CancelledCue : entry.Id;
                entry.Lifetime.Cancel();
                ClearAudio(entry);
                UpdateSceneStatus(device, entry);
                if (entry.Duel is { } duel) { narration[Key(device, duel.ToString("D"))] = "failed"; }
            }
        }
        while (box.Queue.TryPeek(out var head) && Terminal(head)) { box.Queue.Dequeue(); }
    }

    private void Finish(string device, Mailbox box, Entry entry, bool failed)
    {
        entry.Lifetime.Cancel();
        ClearAudio(entry);
        UpdateSceneStatus(device, entry);
        while (box.Queue.TryPeek(out var head) && Terminal(head)) { box.Queue.Dequeue(); }
        if (entry.Duel is { } duel)
        {
            var key = Key(device, duel.ToString("D"));
            if (failed) { narration[key] = "failed"; box.CancelledCue = entry.Id; }
            else if (narration.GetValueOrDefault(key) != "failed")
            { narration[key] = box.Queue.Any(item => item.Duel == duel && !Terminal(item)) ? "pending" : "idle"; }
        }
    }

    private void CancelBox(string device, Mailbox box, string reason)
    {
        _ = reason;
        foreach (var entry in box.Queue)
        {
            if (entry.Descriptor is not null) { box.CancelledCue = entry.Id; }
            entry.Status = "cancelled";
            entry.Lifetime.Cancel();
            ClearAudio(entry);
            UpdateSceneStatus(device, entry);
            if (entry.Duel is { } duel) { narration[Key(device, duel.ToString("D"))] = "failed"; }
        }
        box.Queue.Clear();
    }

    private Mailbox Get(string device)
    {
        if (!show.IsKnownDevice(device)) { throw new ArgumentException("Unknown device."); }
        if (!mailboxes.TryGetValue(device, out var box)) { mailboxes.Add(device, box = new()); }
        return box;
    }

    private void TrimHistory(string device, Mailbox box)
    {
        while (box.History.Count > 32)
        {
            var oldest = box.History.Values.FirstOrDefault(Terminal);
            if (oldest is null) { break; }
            box.History.Remove(oldest.Id);
            box.Known.Remove(oldest.Identity);
            if (oldest.SceneGeneration is { } generation && oldest.SceneSource is { } source)
            { sceneStatuses.TryRemove(SceneKey(device, generation, source), out _); }
            if (oldest.Duel is { } duel && !box.History.Values.Any(entry => entry.Duel == duel))
            { narration.TryRemove(Key(device, duel.ToString("D")), out _); }
            oldest.Lifetime.Dispose();
        }
    }

    private static bool Terminal(Entry entry) => entry.Status is "completed" or "failed" or "cancelled";
    private static bool Matches(Entry entry, Guid boot, int generation, int revision) => entry.Snapshot.BootId == boot
        && entry.Snapshot.Generation == generation && entry.Snapshot.DeviceRevision == revision;
    private static bool ValidPoll(ShowDeviceCuePoll request) => request.BootId != Guid.Empty && request.Generation >= 0
        && request.DeviceRevision >= 0 && request.DeviceTimeMs is >= 0 and <= 9_007_199_254_738_991
        && (request.Voice is null || request.Voice is "marin" or "cedar" or "coral" or "onyx")
        && (request.AppliedSettingsRevision is null && request.AppliedQuiet is null && request.AppliedAmbient is null
            || request.AppliedSettingsRevision is not null && request.AppliedQuiet is not null && request.AppliedAmbient is not null);

    private static void ClearAudio(Entry entry)
    {
        if (entry.Audio is { } audio) { CryptographicOperations.ZeroMemory(audio); entry.Audio = null; }
    }

    private static bool ValidWav(byte[] wav) => wav.Length is >= 46 and <= MaximumWavBytes
        && wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) && wav.AsSpan(8, 8).SequenceEqual("WAVEfmt "u8)
        && BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)) == wav.Length - 8
        && BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(16)) == 16
        && BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20)) == 1
        && BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)) == 1
        && BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)) == 32000
        && BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(28)) == 64000
        && BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(32)) == 2
        && BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)) == 16
        && wav.AsSpan(36, 4).SequenceEqual("data"u8)
        && BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)) == wav.Length - 44 && (wav.Length - 44) % 2 == 0;

    private sealed record Presence(long At, bool SettingsKnown, bool Quiet);
    private sealed class Mailbox
    {
        internal ShowDeviceCuePoll? Poll;
        internal Guid? CancelledCue;
        internal readonly Queue<Entry> Queue = new();
        internal readonly Dictionary<Guid, Entry> History = new();
        internal readonly Dictionary<string, Entry> Known = new(StringComparer.Ordinal);
    }
    private sealed class Entry(Guid id, Guid? duel, int round, string scene, int compartment, string text,
        long epoch, ShowDeviceCuePoll snapshot, long createdAt, string identity)
    {
        internal readonly Guid Id = id;
        internal readonly Guid? Duel = duel;
        internal readonly int Round = round;
        internal readonly string Scene = scene;
        internal readonly int Compartment = compartment;
        internal readonly string Text = text;
        internal readonly long Epoch = epoch;
        internal readonly ShowDeviceCuePoll Snapshot = snapshot;
        internal readonly long CreatedAt = createdAt;
        internal readonly string Identity = identity;
        internal readonly CancellationTokenSource Lifetime = new();
        internal readonly Dictionary<string, string> Acks = new(StringComparer.Ordinal);
        internal string Status = "queued";
        internal ShowDeviceCue? Descriptor;
        internal long? PolledAt;
        internal long? AcceptedAt;
        internal string? AmbientSource;
        internal string? SceneGeneration;
        internal string? SceneSource;
        internal string CueType = "NarratedBeat";
        internal string? Preset;
        internal string? ClipId;
        internal SpeechOptions Speech = SpeechOptions.Resolve(null, null);
        internal bool Claimed;
        internal bool AudioFailed;
        internal string? OperationId;
        internal Task? Work;
        internal byte[]? Audio;
    }
}

internal sealed class ShowDeviceCueWorker(ShowDeviceCueBridge bridge, TimeProvider clock,
    ILogger<ShowDeviceCueWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { bridge.Tick(); }
                catch (Exception error) when (error is not OperationCanceledException)
                { logger.LogWarning("Device cue tick failed ({ErrorType}); existing speech claims are not retried.", error.GetType().Name); }
            }
        }
        finally { bridge.CancelAll(); }
    }
}
