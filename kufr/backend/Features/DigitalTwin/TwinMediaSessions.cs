using System.Globalization;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;

namespace Kufr.Backend.Features.DigitalTwin;

internal sealed partial class TwinMediaSessions : IDisposable
{
    private sealed class Actor(string id, TwinSession owner, TwinMediaStart start, TwinMediaSettings settings, HttpClient http)
    {
        internal readonly string InstanceId = id, OwnerId = owner.Id, OperationId = start.OperationId,
            BootId = start.ExpectedBootId, SessionId = Guid.NewGuid().ToString("D");
        internal readonly DateTimeOffset Expires = owner.Expires;
        internal readonly TwinMediaSettings Settings = settings;
        internal readonly HttpClient Http = http;
        internal readonly CancellationTokenSource Lifetime = new(TimeSpan.FromMinutes(11));
        internal readonly Channel<TwinPrivateReply> Services = Channel.CreateBounded<TwinPrivateReply>(16);
        internal readonly Queue<TwinPrivateReply> Frames = new();
        internal readonly HashSet<string> Requests = new(StringComparer.Ordinal);
        internal readonly Queue<string> RequestOrder = new();
        internal readonly HashSet<string> Operations = new(StringComparer.Ordinal);
        internal readonly SemaphoreSlim Inputs = new(1, 1);
        internal readonly Lock Gate = new();
        internal TwinMediaState? State;
        internal string? ConversationId, StreamId, CancellationError;
        internal bool Cancelled, Starting = true;
        internal TwinFakeVoiceStream? Fake;
    }
    private readonly ConcurrentDictionary<string, Actor> actors = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private readonly ConcurrentDictionary<string, long> stopFences = new(StringComparer.Ordinal);
    // Deterministic race validation only; unset for production. Checkpoints run
    // outside locks and cannot change platform requests, results or decisions.
    internal Func<string, Task>? StartCheckpoint { get; set; }
    private readonly TwinHost hosts;
    private readonly TwinStore snapshots;
    private readonly TwinMediaSettingsStore settings;
    private readonly DeviceAccessStore devices;
    private readonly IConfiguration configuration;
    private readonly TimeProvider clock;
    private readonly ConversationStore conversations;
    public TwinMediaSessions(TwinHost hosts, TwinStore snapshots, TwinMediaSettingsStore settings,
        DeviceAccessStore devices, IConfiguration configuration, TimeProvider clock, ConversationStore conversations)
    {
        this.hosts = hosts; this.snapshots = snapshots; this.settings = settings;
        this.devices = devices; this.configuration = configuration; this.clock = clock;
        this.conversations = conversations;
        hosts.MediaReply += Receive;
    }
    private long StopFence(string id) => stopFences.TryGetValue(id, out var fence) ? fence : 0;
    internal void StopMedia(string id, TwinSession session)
    {
        if (session.Principal.Role != "control" || session.Principal.Mode != "simulation"
            || session.Principal.InstanceId != id || !hosts.IsSimulation(id)) { return; }
        // This fence also covers a start waiting for settings fsync, before an
        // actor exists. Stop never waits for the manager/settings lock.
        stopFences.AddOrUpdate(id, 1, (_, previous) => unchecked(previous + 1));
        if (!actors.TryGetValue(id, out var actor)) { return; }
        InvalidateActor(actor, "media_stopped");
        _ = CleanupActorAsync(actor);
    }
    private Task CheckpointAsync(string stage) => StartCheckpoint?.Invoke(stage) ?? Task.CompletedTask;
    internal bool Busy(string id)
    {
        lock (gate) { return actors.TryGetValue(id, out var actor) && !actor.Cancelled && (actor.Starting || actor.State?.Active == true); }
    }
    internal (TwinMediaSettings? Settings, string? Error) ChangeSettings(string id, TwinSession owner, TwinMediaSettingsChange change)
    {
        lock (gate)
        {
            var claim = hosts.ClaimMedia(id, owner);
            if (claim is not null) { return (null, claim); }
            if (Busy(id) || snapshots.Read(id)?.Snapshot.Reported.State != "idle") { return (null, "settings_device_busy"); }
            return settings.Change(id, change);
        }
    }
    internal async Task<(TwinMediaState? State, string? Error)> StartAsync(string id, TwinSession owner, TwinMediaStart start)
    {
        if (!Guid.TryParseExact(start.OperationId, "D", out _) || !Guid.TryParseExact(start.ExpectedBootId, "D", out _)
            || !start.AudioConsent || !start.MicAvailable || !start.SpeakerAvailable)
        { return (null, "media_consent_and_devices_required"); }
        var fence = StopFence(id);
        await CheckpointAsync("before-reservation");
        TwinMediaSettings selected;
        Actor actor;
        lock (gate)
        {
            if (StopFence(id) != fence) { return (null, "media_cancelled"); }
            var available = settings.Read(id);
            if (available is null) { return (null, "media_unavailable"); }
            selected = available;
            if (selected.Engine == "realtime" && !selected.Availability.Realtime
                || selected.Engine == "gpt-live" && !selected.Availability.GptLive)
            { return (null, "provider_not_authorized_or_configured"); }
            if (start.PhotoConsent != selected.PhotoConsent) { return (null, "media_settings_mismatch"); }
            var claim = hosts.ClaimMedia(id, owner);
            if (claim is not null) { return (null, claim); }
            var current = snapshots.Read(id)?.Snapshot;
            if (current?.Source.BootId != start.ExpectedBootId) { return (null, "media_boot_mismatch"); }
            string? previousConversation = null;
            if (actors.TryGetValue(id, out var previous))
            {
                if (previous.OwnerId == owner.Id && previous.OperationId == start.OperationId && previous.BootId == start.ExpectedBootId)
                { return previous.Cancelled ? (null, "media_cancelled") : (previous.State, previous.State is null ? "media_start_pending" : null); }
                if (!previous.Cancelled && (previous.Starting || previous.State?.Active == true)) { return (null, "media_owned"); }
                previousConversation = previous.ConversationId;
                previous.Lifetime.Cancel(); previous.Http.Dispose();
            }
            var token = devices.IssueToken(id);
            if (token is null) { return (null, "isolated_device_unavailable"); }
            var url = new Uri(configuration["DigitalTwin:IntegrationBackendUrl"]!);
            if (!url.IsLoopback || url.AbsolutePath != "/") { return (null, "isolated_backend_unavailable"); }
            var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = url, Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            actor = new Actor(id, owner, start, selected, http);
            actor.ConversationId = previousConversation;
            actors[id] = actor;
        }
        _ = PumpAsync(actor);
        try
        {
            await CheckpointAsync("before-admission");
            Task<TwinCommandResult> admission;
            lock (actor.Gate)
            {
                if (actor.Cancelled || StopFence(id) != fence)
                {
                    actor.Cancelled = true;
                    admission = Task.FromResult(new TwinCommandResult(false, "media_cancelled"));
                }
                else
                {
                    var engine = selected.Engine == "gpt-live" ? "gpt-live" : "realtime";
                    admission = hosts.MediaCommandAsync(id, $"MEDIA_START {actor.SessionId} {start.OperationId} {engine} {selected.Voice} "
                        + $"{selected.Speed.ToString(CultureInfo.InvariantCulture)} {start.PhotoConsent.ToString().ToLowerInvariant()} {selected.PresentationVariant} "
                        + $"{(start.MicAvailable ? 1 : 0)} {(start.SpeakerAvailable ? 1 : 0)} {(start.CameraAvailable ? 1 : 0)}");
                }
            }
            var result = await admission;
            await CheckpointAsync("after-admission");
            if (!result.Accepted) { await CancelActorAsync(actor); return (null, result.Code); }
            return actor.Cancelled ? (null, "media_cancelled") : (actor.State, actor.State is null ? "media_state_unavailable" : null);
        }
        catch (Exception error) when (error is TimeoutException or IOException or OperationCanceledException or InvalidOperationException)
        {
            // Native START may already have been queued or accepted. Invalidate
            // immediately, then enqueue cancellation behind it; never retry it.
            await CancelActorAsync(actor);
            return (null, "media_admission_outcome_unknown");
        }
        finally { lock (actor.Gate) { actor.Starting = false; } }
    }
    internal TwinMediaState? State(string id, TwinSession owner) => Owned(id, owner)?.State;
    internal async Task<(TwinPrivateReply? Reply, string? Error)> OutputAsync(string id, TwinSession owner)
    {
        var actor = Owned(id, owner);
        if (actor is null) { return (null, "media_owner_required"); }
        TwinMediaState? state;
        lock (actor.Gate)
        {
            if (actor.Frames.TryDequeue(out var frame)) { return (frame, null); }
            state = actor.State;
        }
        if (state?.SessionId is null || actor.Cancelled) { return (null, null); }
        var result = await hosts.MediaCommandAsync(id, $"MEDIA_TAKE {actor.SessionId} {state.Epoch}");
        if (!result.Accepted) { return (null, result.Code); }
        lock (actor.Gate) { return (actor.Frames.TryDequeue(out var frame) ? frame : null, null); }
    }
    internal async Task<string?> CancelAsync(string id, TwinSession owner, TwinMediaCancel cancel)
    {
        var actor = Owned(id, owner);
        if (actor is null) { return "media_owner_required"; }
        if (cancel.OperationId is not null)
        {
            if (cancel.OperationId != actor.OperationId || cancel.ExpectedBootId != actor.BootId) { return "media_admission_mismatch"; }
        }
        else if (!Bound(actor, cancel.SessionId, cancel.BootId, cancel.Epoch)) { return "media_epoch_mismatch"; }
        await CancelActorAsync(actor);
        return null;
    }
    private void InvalidateActor(Actor actor, string error)
    {
        lock (actor.Gate)
        {
            actor.Cancelled = true; actor.Starting = false; actor.CancellationError = error; actor.Frames.Clear();
            if (actor.State is { } state) { actor.State = state with { Active = false, Phase = "idle", Error = error }; }
        }
        _ = actor.Lifetime.CancelAsync();
    }
    private Task CancelActorAsync(Actor actor)
    {
        InvalidateActor(actor, "media_cancelled");
        return CleanupActorAsync(actor);
    }
    private async Task CleanupActorAsync(Actor actor)
    {
        try { await hosts.MediaCommandAsync(actor.InstanceId, "MEDIA_CANCEL " + actor.SessionId); }
        catch (Exception error) when (error is TimeoutException or IOException or InvalidOperationException or OperationCanceledException)
        { /* Native outcome is uncertain; the actor and HTTP lifetime stay invalid. */ }
        finally { _ = CloseCancelledAsync(actor); }
    }
    private Actor? Owned(string id, TwinSession owner)
    {
        if (owner.Principal.Role != "control" || owner.Principal.Mode != "simulation" || owner.Principal.InstanceId != id) { return null; }
        lock (gate) { return actors.TryGetValue(id, out var actor) && actor.OwnerId == owner.Id && actor.Expires > clock.GetUtcNow() ? actor : null; }
    }
    private static bool Bound(Actor actor, string? session, string? boot, string? epoch) => !actor.Cancelled
        && session == actor.SessionId && boot == actor.BootId && epoch == actor.State?.Epoch;
    private void Receive(string id, TwinPrivateReply reply)
    {
        // Host reply publication never waits for settings fsync/start admission.
        if (!actors.TryGetValue(id, out var actor)) { return; }
        bool overflow = false, invalidated = false;
        lock (actor.Gate)
        {
            if (reply.State is { } incoming && incoming.BootId != actor.BootId)
            {
                actor.Cancelled = true; actor.Frames.Clear(); invalidated = true;
                if (actor.State is { } oldState) { actor.State = oldState with { Active = false, Phase = "failed", Error = "media_boot_changed" }; }
            }
            if (reply.State?.SessionId == actor.SessionId && reply.State.BootId == actor.BootId)
            {
                if (actor.State is { } old && old.Epoch != reply.State.Epoch)
                {
                    actor.Frames.Clear();
                    if (!reply.State.Active) { actor.Cancelled = true; invalidated = true; }
                }
                if (actor.State is { } outputState && outputState.OutputEpoch != reply.State.OutputEpoch)
                { actor.Frames.Clear(); }
                // A late accepted START/result may still be in the native FIFO
                // after cancellation. Preserve its observed epochs, but never
                // advertise a cancelled actor as active again.
                actor.State = actor.Cancelled ? reply.State with
                {
                    Active = false, Phase = actor.State?.Phase ?? "idle",
                    Error = actor.CancellationError ?? actor.State?.Error ?? "media_cancelled"
                } : reply.State;
                if (!reply.State.Active && reply.State.Phase == "failed") { actor.Cancelled = true; actor.Frames.Clear(); invalidated = true; }
            }
            if (reply.Media is { } media && Bound(actor, media.SessionId, media.BootId, media.Epoch)
                && media.OutputEpoch == actor.State?.OutputEpoch)
            {
                if (actor.Frames.Count >= 8) { overflow = true; }
                else { actor.Frames.Enqueue(reply); }
            }
            if (reply.Service is { } service && (!actor.Cancelled || service.Kind == "conversation-delete"))
            {
                if (!actor.Requests.Add(service.RequestId)) { overflow = true; }
                else
                {
                    actor.RequestOrder.Enqueue(service.RequestId);
                    if (actor.RequestOrder.Count > 256) { actor.Requests.Remove(actor.RequestOrder.Dequeue()); }
                    if (!actor.Services.Writer.TryWrite(reply)) { overflow = true; }
                }
            }
            if (overflow)
            {
                actor.Cancelled = true;
                actor.Frames.Clear();
                if (actor.State is { } current) { actor.State = current with { Active = false, Error = "media_queue_overflow", Phase = "failed" }; }
            }
        }
        if (invalidated) { actor.Lifetime.Cancel(); _ = CloseCancelledAsync(actor); }
        if (overflow) { _ = CancelActorAsync(actor); }
    }
    public void Dispose()
    {
        hosts.MediaReply -= Receive;
        lock (gate)
        {
            foreach (var actor in actors.Values)
            {
                actor.Lifetime.Cancel();
                if (actor.ConversationId is { } conversation)
                { conversations.EndConversation(actor.InstanceId, conversation, Guid.NewGuid()); }
                actor.Http.Dispose();
            }
        }
    }
}
