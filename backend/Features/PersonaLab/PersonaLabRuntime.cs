using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Realtime;

namespace Kufr.Backend.Features.PersonaLab;

internal sealed record LabAdmission(string Owner, string SessionId, string AccountSessionId, string SecurityStamp,
    string Origin, DateTimeOffset ExpiresAt, bool Recovery = false, string? Platform = null);
internal sealed class LabTicketRegistry(TimeProvider? clock = null)
{
    private readonly ConcurrentDictionary<string, LabAdmission> tickets = new(StringComparer.Ordinal);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    internal (string Token, DateTimeOffset ExpiresAt) Mint(LabAdmission admission)
    {
        LabContract.ExecutionPlatform(admission.Platform);
        foreach (var expired in tickets.Where(x => x.Value.ExpiresAt <= time.GetUtcNow())) { tickets.TryRemove(expired.Key, out _); }
        if (tickets.Count >= 1000) { throw new LabException(429, "lab_ticket_capacity"); }
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var expires = time.GetUtcNow().AddSeconds(30);
        tickets[LabContract.Hash(token)] = admission with { ExpiresAt = expires };
        return (token, expires);
    }
    internal LabAdmission Take(string token, string session, string origin)
    {
        if (token.Length != 64 || !tickets.TryRemove(LabContract.Hash(token), out var admission)
            || admission.SessionId != session || admission.Origin != origin || admission.ExpiresAt <= time.GetUtcNow())
        {
            throw new LabException(401, "invalid_lab_ticket");
        }
        return admission;
    }
}

internal sealed class PersonaLabRuntime(PersonaLabStore store, GptLiveClient live, OpenAiRealtimeClient realtime,
    PersonaLabImages? images = null, PersonaLabOptions? options = null) : IDisposable
{
    private readonly PersonaLabStore store = store;
    private readonly Lock admissionGate = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> active = new(StringComparer.Ordinal);
    private readonly HashSet<CancellationToken> inputRevocations = [];
    private readonly ConcurrentDictionary<string, LabTurn[]> ephemeral = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> ephemeralExpiry = new(StringComparer.Ordinal);
    private Timer? ephemeralCleanup;
    private bool stopping;
    private readonly Dictionary<string, Guid> scriptReservations = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim capacity = new(options?.MaxConcurrentSessions ?? 4, options?.MaxConcurrentSessions ?? 4);
    internal bool LiveAvailable => live.Available;
    internal bool RealtimeAvailable => realtime.Available;
    internal int ActiveCount => active.Count;
    internal bool IsActive(string id) => active.ContainsKey(id);
    internal IDisposable ReserveScript(string owner, string id, out Guid scriptOwner)
    {
        lock (admissionGate)
        {
            if (stopping) { throw new LabException(503, "lab_runtime_stopping"); }
            var session = Detail(owner, id);
            if (session.Status != "open") { throw new LabException(409, "lab_session_closed"); }
            if (session.Hardware is not null) { throw new LabException(409, "lab_hardware_bound"); }
            if (active.ContainsKey(id) || scriptReservations.ContainsKey(id))
            { throw new LabException(409, "lab_session_busy"); }
            var reservation = Guid.NewGuid();
            scriptReservations.Add(id, reservation);
            scriptOwner = reservation;
            return new ScriptReservation(() =>
            {
                lock (admissionGate)
                {
                    if (scriptReservations.TryGetValue(id, out var current) && current == reservation)
                    { scriptReservations.Remove(id); }
                }
            });
        }
    }
    internal void ObserveHardware(LabSession session, LabEvent observation) => SaveEvent(session, observation);
    internal LabTurn ObserveHardwareTurn(LabSession session, string role, string text, long? latencyMs = null,
        int? audioBytes = null, string? decision = null, int? compartment = null) =>
        // Hardware bindings refuse image context; their existing session/epoch fences own this route.
        SaveTurn(session, role, text, CancellationToken.None, latencyMs, audioBytes, decision, compartment);
    internal LabHardwareLease ReserveHardware(string owner, string id)
    {
        lock (admissionGate)
        {
            if (stopping) { throw new LabException(503, "lab_runtime_stopping"); }
            var session = Detail(owner, id);
            if (session.Status != "open") { throw new LabException(409, "lab_session_closed"); }
            if (session.Hardware is not null) { throw new LabException(409, "lab_hardware_bound"); }
            if (active.ContainsKey(id) || scriptReservations.ContainsKey(id)) { throw new LabException(409, "lab_session_busy"); }
            if (session.HasImage) { throw new LabException(409, "lab_hardware_image_context_unsupported"); }
            if (session.Engine == GptLiveProtocol.Model ? !LiveAvailable : !RealtimeAvailable)
            { throw new LabException(503, "lab_provider_unavailable"); }
            if (!capacity.Wait(0)) { throw new LabException(429, "lab_concurrency_limit"); }
            var lifetime = new CancellationTokenSource();
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(options?.MaxSessionSeconds ?? 180);
            lifetime.CancelAfter(expiresAt - DateTimeOffset.UtcNow);
            active[id] = lifetime;
            return new(session, lifetime, () =>
            {
                lock (admissionGate)
                {
                    if (active.TryGetValue(id, out var current) && ReferenceEquals(current, lifetime))
                    {
                        active.TryRemove(id, out _);
                        ephemeral.TryRemove(id, out _);
                        ephemeralExpiry.TryRemove(id, out _);
                        images?.Clear(owner, id);
                        inputRevocations.Remove(lifetime.Token);
                        capacity.Release();
                    }
                }
            }, expiresAt);
        }
    }
    internal LabSession Detail(string owner, string id)
    {
        ExpireEphemeral();
        var session = store.Session(owner, id) with { HasImage = images?.HasImage(owner, id) == true };
        return !session.RecordContent && ephemeral.TryGetValue(id, out var turns)
            ? session with { Turns = session.Turns.Select(turn => turns.FirstOrDefault(transient => transient.Id == turn.Id) ?? turn).ToArray() }
            : session;
    }
    internal LabSession End(string owner, string id)
    {
        lock (admissionGate)
        {
            store.Session(owner, id);
            if (active.TryGetValue(id, out var lifetime)) { lifetime.Cancel(); }
            ephemeral.TryRemove(id, out _);
            ephemeralExpiry.TryRemove(id, out _);
            images?.Clear(owner, id);
            return store.Change(owner, id, value => value.Status == "open"
                ? value with { Status = "ended", EndedAt = DateTimeOffset.UtcNow,
                    Events = [.. value.Events, LabContract.Event("session_ended")] } : value);
        }
    }
    internal void Delete(string owner, string id)
    {
        lock (admissionGate)
        {
            store.Session(owner, id);
            if (active.ContainsKey(id)) { throw new LabException(409, "lab_session_busy"); }
            ephemeral.TryRemove(id, out _);
            images?.Clear(owner, id);
            store.DeleteSession(owner, id);
        }
    }
    internal LabSession ChangeImage(string owner, string id, Action<LabSession> change, bool revoke = false)
    {
        lock (admissionGate)
        {
            var session = store.Session(owner, id);
            if (session.Engine != RealtimeProtocol.Model) { throw new LabException(409, "lab_image_requires_realtime"); }
            if (!revoke && session.Hardware is not null) { throw new LabException(409, "lab_hardware_bound"); }
            if (!revoke && scriptReservations.ContainsKey(id)) { throw new LabException(409, "lab_session_busy"); }
            if (!revoke && session.Status != "open") { throw new LabException(409, "lab_session_closed"); }
            if (active.TryGetValue(id, out var current))
            {
                if (!revoke) { throw new LabException(409, "lab_session_busy"); }
                inputRevocations.Add(current.Token);
                current.Cancel();
            }
            change(session);
            SaveEvent(session, LabContract.Event("image_context", revoke ? "revoked" : "provided"));
            if (revoke && scriptReservations.ContainsKey(id)) { return End(owner, id); }
            return Detail(owner, id);
        }
    }
    internal async Task<LabSession> TurnAsync(string owner, string id, string text, CancellationToken token,
        Guid? scriptOwner = null, string? platform = null)
    {
        LabContract.Text(text, 4000);
        return await RunAsync(owner, id, async (session, lifetime) =>
        {
            var context = Context(session, text, out var omitted);
            if (omitted > 0) { SaveEvent(session, LabContract.Event("context_trimmed", "oldest_turns_omitted")); }
            await using var transport = await ConnectAsync(session, lifetime);
            var timer = Stopwatch.StartNew();
            var audioBytes = 0;
            if (session.Engine == RealtimeProtocol.Model)
            {
                var reply = await transport.Realtime!.GenerateTurnAsync(context, null,
                    (pcm, _) => { audioBytes += pcm.Length; return Task.CompletedTask; }, (_, _) => Task.CompletedTask, lifetime,
                    visualImage: () => images?.Read(owner, id));
                SaveEvent(session, LabContract.Event("provider_session", "ready", providerSessionId: transport.Realtime.ProviderSessionId));
                SaveEvent(session, LabContract.Event("provider_cost", "micro_usd", costMicroUsd: reply.CostMicroUsd));
                RecordTokens(session, transport.Realtime);
                SaveTurn(session, "user", text, lifetime);
                SaveTurn(session, "assistant", reply.Reply, lifetime, timer.ElapsedMilliseconds, audioBytes, reply.Decision, reply.Compartment);
                SaveEvent(session, LabContract.Event("provider_turn", "completed", timer.ElapsedMilliseconds, reply.Decision, reply.Compartment, reply.CostMicroUsd));
                RecordProposal(session, reply);
            }
            else
            {
                SaveTurn(session, "user", text, lifetime);
                await transport.Live!.SendAsync(GptLiveProtocol.AutonomousStart(context, transport.StartId), lifetime);
                await StartLiveAsync(transport, lifetime);
                SaveEvent(session, LabContract.Event("provider_session", "ready", providerSessionId: transport.Live.ProviderSessionId));
                // Live advances on the input-audio timeline even when the test
                // visitor supplied text. Pace real PCM silence rather than
                // sending a burst or waiting for audio that will never arrive.
                using var inputLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                var uploading = SendSilenceAsync(transport.Live, inputLifetime.Token);
                try { await ReadLiveTurnAsync(session, transport.Live, timer, uploading, StopInputAsync, lifetime); }
                finally { await StopInputAsync(); }

                async Task StopInputAsync()
                {
                    inputLifetime.Cancel();
                    try { await uploading; }
                    catch (OperationCanceledException) when (inputLifetime.IsCancellationRequested) { }
                }
            }
            return Detail(owner, id);
        }, token, scriptOwner, platform);
    }

    internal async Task VoiceAsync(string owner, string id, WebSocket socket,
        Func<CancellationToken, Task<bool>> authorized, CancellationToken requestToken, bool recovery = false,
        bool acknowledgedPlayback = false, string? platform = null)
    {
        LabContract.ExecutionPlatform(platform);
        if (!await authorized(requestToken)) { throw new LabException(401, "account_session_revoked"); }
        await RunAsync(owner, id, async (session, lifetime) =>
        {
            using var socketLife = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            var token = socketLife.Token;
            if (recovery) { SaveEvent(session, LabContract.Event("recovery_started", "explicit_retry")); }
            await using var transport = await ConnectAsync(session, token);
            using var realtimeInput = transport.Realtime is null ? null
                : new PersonaLabRealtimeInput(transport.Realtime.SendAudioAsync);
            var sender = new LabSocketSender(socket, authorized);
            var outputSuppressed = 0;
            Stopwatch? responseClock = null;
            var playback = acknowledgedPlayback && transport.Realtime is not null
                ? new PersonaLabPlayback(sender.JsonAsync,
                    (playbackId, played, cancellation) => realtimeInput!.InterruptAsync(() =>
                    {
                        Volatile.Write(ref outputSuppressed, 1);
                        return transport.Realtime.InterruptResponseAsync(playbackId, played, cancellation);
                    }), realtimeInput!.PrepareNextListening,
                    wait => { if (wait) { responseClock?.Stop(); } else { responseClock?.Start(); } })
                : null;
            var timer = Stopwatch.StartNew();
            var inputBytes = 0;
            var outputBytes = 0;
            var providerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            double minimumOutputStart = 0;
            long lastInputAt = -1;
            Task receive = ReceiveAsync();
            Task provider = session.Engine == GptLiveProtocol.Model ? LiveAsync() : RealtimeAsync();
            Task guard = GuardAsync();
            try
            {
                await Task.WhenAny(receive, provider, guard);
                socketLife.Cancel();
                await Task.WhenAll(receive, provider, guard);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally
            {
                socketLife.Cancel();
                if (!session.RecordContent) { ephemeral.TryRemove(id, out _); }
            }
            return Detail(owner, id);

            async Task GuardAsync()
            {
                while (!token.IsCancellationRequested)
                {
                    if (!await authorized(token)) { throw new LabException(401, "account_session_revoked"); }
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                }
            }
            async Task ReceiveAsync()
            {
                var buffer = new byte[32768];
                while (!token.IsCancellationRequested)
                {
                    var received = await socket.ReceiveAsync(buffer.AsMemory(), token);
                    if (received.MessageType == WebSocketMessageType.Close) { return; }
                    if (!await authorized(token)) { throw new LabException(401, "account_session_revoked"); }
                    if (!received.EndOfMessage) { throw new InvalidDataException("Fragmented lab frame exceeds contract."); }
                    if (received.MessageType == WebSocketMessageType.Binary)
                    {
                        inputBytes = checked(inputBytes + received.Count);
                        if (received.Count % 2 != 0 || inputBytes > 24_000 * 2 * 180)
                        {
                            throw new LabException(413, "lab_audio_limit");
                        }
                        var pcm = buffer.AsSpan(0, received.Count).ToArray();
                        try
                        {
                            await providerReady.Task.WaitAsync(token);
                            if (transport.Live is not null) { await transport.Live.SendAudioAsync(pcm, token); }
                            else { await realtimeInput!.SendAsync(pcm, token); }
                        }
                        finally { CryptographicOperations.ZeroMemory(pcm); }
                        continue;
                    }
                    using var message = JsonDocument.Parse(buffer.AsMemory(0, received.Count));
                    var type = GptLiveProtocol.RequiredString(message.RootElement, "type", 20);
                    if (type == "end") { return; }
                    if (playback is not null && type is "playback_done" or "playback_error")
                    {
                        var playbackId = GptLiveProtocol.RequiredString(message.RootElement, "playbackId", 128);
                        if (!playback.IsCurrent(playbackId)) { continue; }
                        if (type == "playback_error")
                        {
                            var code = GptLiveProtocol.RequiredString(message.RootElement, "code", 40);
                            if (code is not ("audio_backpressure" or "audio_stalled" or "audio_interrupted"))
                            {
                                throw new LabException(400, "invalid_lab_playback");
                            }
                            if (playback.IsCurrent(playbackId))
                            {
                                SaveEvent(session, LabContract.Event("client_playback_failure", code));
                                throw new LabException(502, "lab_" + code);
                            }
                        }
                        else
                        {
                            if (!message.RootElement.TryGetProperty("playedSamples", out var samples)
                                || !samples.TryGetInt32(out var playedSamples)
                                || playedSamples is < 0 or > RealtimeProtocol.MaxAudioBytes / 2)
                            {
                                throw new LabException(400, "invalid_lab_playback");
                            }
                            playback.Acknowledge(playbackId, playedSamples);
                        }
                        continue;
                    }
                    if (type == "cancel")
                    {
                        if (transport.Live is not null)
                        {
                            Volatile.Write(ref outputSuppressed, 1);
                            await transport.Live.SendAsync(new { type = "session.instructions.append", event_id = Guid.NewGuid().ToString("N"), delegation_id = (string?)null,
                                content = "The visitor interrupted. Stop the current utterance and listen. Do not resume discarded speech. Wait for the visitor to finish." }, token);
                            await sender.JsonAsync(new { type = "interrupted" }, token);
                            SaveEvent(session, LabContract.Event("provider_interrupt", "live_local_generation_fence"));
                        }
                        else
                        {
                            if (playback is not null)
                            {
                                var playbackId = GptLiveProtocol.RequiredString(message.RootElement, "playbackId", 128);
                                if (!playback.IsCurrent(playbackId)) { continue; }
                                if (!message.RootElement.TryGetProperty("playedAudioMs", out var playedPosition)
                                    || !playedPosition.TryGetInt32(out var playedMs)
                                    || playedMs is < 0 or > RealtimeProtocol.MaxAudioBytes / 48)
                                {
                                    throw new LabException(400, "invalid_lab_playback");
                                }
                                await playback.CancelAsync(playbackId, playedMs, token);
                                continue;
                            }
                            var played = message.RootElement.TryGetProperty("playedAudioMs", out var position) && position.TryGetInt32(out var milliseconds)
                                && milliseconds is >= 0 and <= 180000 ? milliseconds : 0;
                            if (!await realtimeInput!.InterruptAsync(() =>
                            {
                                Volatile.Write(ref outputSuppressed, 1);
                                var cancellation = transport.Realtime!.InterruptResponseAsync(played, token);
                                if (cancellation.IsCompletedSuccessfully && !cancellation.Result)
                                {
                                    Volatile.Write(ref outputSuppressed, 0);
                                }
                                return cancellation;
                            }))
                            {
                                Volatile.Write(ref outputSuppressed, 0);
                                await sender.JsonAsync(new { type = "interrupted" }, token);
                            }
                        }
                        continue;
                    }
                    if (type != "text") { throw new LabException(400, "invalid_lab_command"); }
                    throw new LabException(409, "lab_text_requires_http");
                }
            }
            async Task LiveAsync()
            {
                long accountedCost = 0;
                var context = Context(session, "", out var omitted);
                if (omitted > 0) { SaveEvent(session, LabContract.Event("context_trimmed", "oldest_turns_omitted")); }
                await transport.Live!.SendAsync(GptLiveProtocol.AutonomousStart(context, transport.StartId), token);
                await StartLiveAsync(transport, token);
                SaveEvent(session, LabContract.Event("provider_session", "ready", providerSessionId: transport.Live.ProviderSessionId));
                if (recovery) { SaveEvent(session, LabContract.Event("recovery_completed", "provider_ready")); }
                providerReady.TrySetResult();
                if (acknowledgedPlayback) { await sender.JsonAsync(new { type = "ready", playbackMode = "stream" }, token); }
                else { await sender.JsonAsync(new { type = "ready" }, token); }
                await transport.Live.SendAsync(new { type = "session.instructions.append", event_id = Guid.NewGuid().ToString("N"), delegation_id = (string?)null,
                    content = "Begin this fictional test interaction now. Greet briefly, ask one question, then listen." }, token);
                var transcript = new LiveTranscriptCollector(this, session, sender, token);
                for (var count = 0; count < 8192; count++)
                {
                    using var document = await transport.Live.ReceiveAsync(token);
                    var value = document.RootElement;
                    var kind = GptLiveProtocol.RequiredString(value, "type", 100);
                    if (kind == "session.output_audio.delta")
                    {
                        var pcm = Convert.FromBase64String(GptLiveProtocol.RequiredString(value, "delta", 180_000));
                        try
                        {
                            outputBytes = checked(outputBytes + pcm.Length);
                            if (pcm.Length % 2 != 0 || outputBytes > 24000 * 2 * 190) { throw new LabException(413, "lab_audio_limit"); }
                            if (Volatile.Read(ref outputSuppressed) == 0) { await sender.AudioAsync(pcm, token); }
                        }
                        finally { CryptographicOperations.ZeroMemory(pcm); }
                    }
                    else if (kind is "session.input_transcript.delta" or "session.output_transcript.delta")
                    {
                        var input = kind.Contains("input_", StringComparison.Ordinal);
                        if (input)
                        {
                            minimumOutputStart = GptLiveProtocol.Milliseconds(value, "end_ms");
                            lastInputAt = timer.ElapsedMilliseconds;
                        }
                        else if (Volatile.Read(ref outputSuppressed) != 0)
                        {
                            if (lastInputAt < 0 || timer.ElapsedMilliseconds - lastInputAt < 1200
                                || GptLiveProtocol.Milliseconds(value, "start_ms") < minimumOutputStart) { continue; }
                            Volatile.Write(ref outputSuppressed, 0);
                        }
                        await transcript.AppendAsync(input ? "user" : "assistant", GptLiveProtocol.RequiredString(value, "delta", 16000), token);
                    }
                    else if (kind == "session.delegation.created")
                    {
                        await SimulateDelegationAsync(session, transport.Live, value, token);
                    }
                    else if (kind == "error") { throw LiveProviderFailure.Read(value); }
                    else if (kind == "session.usage.updated") { RecordUsage(session, value, ref accountedCost); }
                    else if (kind == "session.closed")
                    {
                        RecordUsage(session, value, ref accountedCost, required: true);
                        SaveEvent(session, LabContract.Event("provider_usage_final", "received"));
                        return;
                    }
                }
                throw new LabException(409, "lab_event_limit");
            }
            async Task RealtimeAsync()
            {
                // Begin with a genuine VAD listening turn so the first microphone
                // frame cannot race an opening-turn clear/configuration cycle.
                var opening = false;
                for (var count = 0; count < 100; count++)
                {
                    // The existing Realtime connection owns provider VAD and input transcription.
                    var context = Context(Detail(owner, id), "", out var omitted, liveAudio: !opening);
                    if (omitted > 0) { SaveEvent(session, LabContract.Event("context_trimmed", "oldest_turns_omitted")); }
                    if (opening) { context = context with { Request = context.Request with { Opening = true } }; }
                    RealtimeReply reply;
                    var responseTimer = Stopwatch.StartNew();
                    responseClock = responseTimer;
                    try
                    {
                        reply = await transport.Realtime!.GenerateTurnAsync(context, null,
                        async (pcm, cancellation) =>
                        {
                            outputBytes = checked(outputBytes + pcm.Length);
                            if (outputBytes > 24000 * 2 * 190) { throw new LabException(413, "lab_audio_limit"); }
                            if (Volatile.Read(ref outputSuppressed) == 0) { await sender.AudioAsync(pcm, cancellation); }
                        },
                        async (input, cancellation) =>
                        {
                            responseTimer.Restart();
                            var turn = SaveTurn(session, "user", input, token);
                            await sender.JsonAsync(new { type = "turn", turn }, cancellation);
                        }, token,
                        listening: async cancellation =>
                        {
                            await realtimeInput!.ListenAsync(cancellation);
                            if (providerReady.TrySetResult())
                            {
                                SaveEvent(session, LabContract.Event("provider_session", "ready", providerSessionId: transport.Realtime.ProviderSessionId));
                                if (recovery) { SaveEvent(session, LabContract.Event("recovery_completed", "provider_ready")); }
                                if (acknowledgedPlayback)
                                {
                                    await sender.JsonAsync(new { type = "ready", playbackMode = "acknowledged" }, cancellation);
                                }
                                else { await sender.JsonAsync(new { type = "ready" }, cancellation); }
                            }
                            if (playback is not null) { await sender.JsonAsync(new { type = "listening" }, cancellation); }
                        },
                        speech: async (state, cancellation) =>
                        {
                            if (state == "speech_stopped") { await realtimeInput!.StopAsync(cancellation); }
                            await sender.JsonAsync(new { type = "speech", state }, cancellation);
                        },
                        visualImage: () => images?.Read(owner, id), playback: playback);
                    }
                    catch (RealtimeResponseInterruptedException interrupted)
                    {
                        opening = false;
                        Volatile.Write(ref outputSuppressed, 0);
                        SaveEvent(session, LabContract.Event("provider_interrupt", "realtime_cancelled", costMicroUsd: interrupted.CostMicroUsd));
                        SaveEvent(session, LabContract.Event("provider_cost", "micro_usd", costMicroUsd: interrupted.CostMicroUsd));
                        RecordTokens(session, transport.Realtime!);
                        await sender.JsonAsync(new { type = "interrupted" }, token);
                        continue;
                    }
                    catch
                    {
                        if (playback is not null)
                        {
                            SaveEvent(session, LabContract.Event("provider_cost", "micro_usd", costMicroUsd: transport.Realtime!.LastCostMicroUsd));
                            RecordTokens(session, transport.Realtime);
                        }
                        throw;
                    }
                    // A drained reply is already observed provider work. Keep
                    // its usage even if the final client notification fails.
                    SaveEvent(session, LabContract.Event("provider_cost", "micro_usd", costMicroUsd: reply.CostMicroUsd));
                    RecordTokens(session, transport.Realtime!);
                    var assistant = SaveTurn(session, "assistant", reply.Reply, token, responseTimer.ElapsedMilliseconds, null, reply.Decision, reply.Compartment);
                    SaveEvent(session, LabContract.Event("provider_turn", "completed", responseTimer.ElapsedMilliseconds, reply.Decision, reply.Compartment, reply.CostMicroUsd));
                    RecordProposal(session, reply);
                    await sender.JsonAsync(new { type = "turn", turn = assistant }, token);
                    opening = false;
                }
                throw new LabException(409, "lab_turn_limit");
            }
        }, requestToken, platform: platform);
    }

    private async Task<T> RunAsync<T>(string owner, string id, Func<LabSession, CancellationToken, Task<T>> action,
        CancellationToken requestToken, Guid? scriptOwner = null, string? platform = null)
    {
        var executionPlatform = LabContract.ExecutionPlatform(platform);
        var session = Detail(owner, id);
        if (session.Status != "open") { throw new LabException(409, "lab_session_closed"); }
        if (session.Hardware is not null) { throw new LabException(409, "lab_hardware_bound"); }
        if ((session.Engine == GptLiveProtocol.Model && !live.Available)
            || (session.Engine == RealtimeProtocol.Model && !realtime.Available)) { throw new LabException(503, "lab_provider_unavailable"); }
        if (!await capacity.WaitAsync(0, requestToken)) { throw new LabException(429, "lab_concurrency_limit"); }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
        lifetime.CancelAfter(TimeSpan.FromSeconds(options?.MaxSessionSeconds ?? 180));
        try
        {
            lock (admissionGate)
            {
                if (stopping) { throw new LabException(503, "lab_runtime_stopping"); }
                if (scriptReservations.TryGetValue(id, out var reserved) && reserved != scriptOwner)
                { throw new LabException(409, "lab_session_busy"); }
                session = Detail(owner, id);
                if (session.Hardware is not null) { throw new LabException(409, "lab_hardware_bound"); }
                if (session.Status != "open" || !active.TryAdd(id, lifetime))
                { throw new LabException(409, session.Status == "open" ? "lab_session_busy" : "lab_session_closed"); }
                try
                {
                    // Persist admission independently of the capped event stream, under the same fence as End.
                    store.Change(owner, id, current => current.Status == "open"
                        ? current with { ExecutionPlatforms = [.. current.ExecutionPlatforms, executionPlatform] }
                        : throw new LabException(409, "lab_session_closed"));
                    session = Detail(owner, id);
                }
                catch
                {
                    active.TryRemove(id, out _);
                    throw;
                }
            }
        }
        catch { capacity.Release(); throw; }
        try { return await action(session, lifetime.Token); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var cancelled = error is OperationCanceledException;
            bool inputRevoked;
            lock (admissionGate) { inputRevoked = cancelled && inputRevocations.Contains(lifetime.Token); }
            var userCancelled = cancelled && (requestToken.IsCancellationRequested || store.Session(owner, id).Status == "ended");
            var invalidInput = error is LabException { Status: 400 };
            var code = invalidInput ? "invalid_request" : inputRevoked ? "input_revoked" : userCancelled ? "user_cancelled" : cancelled ? "timeout"
                : error is WebSocketException or IOException ? "transport_error"
                : session.Engine == GptLiveProtocol.Model && error is not LabException ? LiveFailureDiagnostics.Code(error)
                    : session.Engine == RealtimeProtocol.Model ? PersonaLabFailureDiagnostics.RealtimeCode(error) : "provider_error";
            SaveEvent(session, LabContract.Event(invalidInput ? "request_rejected" : inputRevoked ? "request_cancelled" : "provider_failure", code));
            if (code == "transport_error") { SaveEvent(session, LabContract.Event("connection_interrupted", "unexpected_close")); }
            if (error is LabException) { throw; }
            throw new LabException(inputRevoked ? 409 : error is OperationCanceledException ? 408 : 502,
                inputRevoked ? "lab_input_revoked" : error is OperationCanceledException ? "lab_provider_timeout"
                    : session.Engine == GptLiveProtocol.Model || code.StartsWith("realtime_", StringComparison.Ordinal)
                        ? "lab_" + code : "lab_provider_error");
        }
        finally
        {
            lock (admissionGate)
            {
                active.TryRemove(id, out _);
                inputRevocations.Remove(lifetime.Token);
            }
            capacity.Release();
        }
    }
    private async Task<LabProviderTransport> ConnectAsync(LabSession session, CancellationToken token)
    {
        SaveEvent(session, LabContract.Event("connection_started", session.Engine == GptLiveProtocol.Model ? "live" : "realtime"));
        var timer = Stopwatch.StartNew();
        var transport = session.Engine == GptLiveProtocol.Model
            ? new LabProviderTransport(await live.ConnectAsync(token), null)
            : new LabProviderTransport(null, await realtime.ConnectAsync(token));
        SaveEvent(session, LabContract.Event("connection_established", "connected", timer.ElapsedMilliseconds));
        return transport;
    }

    private static RealtimeTurnContext Context(LabSession session, string text, out int omitted, bool liveAudio = false)
    {
        var options = new GptLiveOptions();
        var instructions = session.Config.Instructions + "\n" + LabContract.SafetyInstructions;
        if (LiveTextBudget.UpperBound(instructions) > 16_384 || instructions.Length > 16_000)
        { throw new LabException(400, "lab_instruction_limit"); }
        var snapshot = new LivePromptSnapshot("persona-lab", "persona-lab-v1", LabContract.Hash(instructions),
            instructions, new(2, 3, true), options);
        var retained = session.Turns.Where(turn => turn.Text.Length > 0).TakeLast(30).ToList();
        var originalCount = session.Turns.Count(turn => turn.Text.Length > 0);
        var history = Input();
        while (LiveTextBudget.UpperBound(JsonSerializer.Serialize(history)) > 8192 && retained.Count > 0)
        {
            retained.RemoveAt(0);
            history = Input();
        }
        if (LiveTextBudget.UpperBound(JsonSerializer.Serialize(history)) > 8192)
        { throw new LabException(400, "lab_input_context_limit"); }
        omitted = originalCount - retained.Count;
        var exchanges = new List<DialogExchange>();
        string? previousInput = null;
        foreach (var turn in retained)
        {
            if (turn.Role == "user") { previousInput = turn.Text; }
            else if (turn.Role == "assistant")
            {
                exchanges.Add(new(previousInput ?? "", turn.Text));
                previousInput = null;
            }
        }
        var request = new RealtimeTurnRequest(Guid.NewGuid(), Guid.NewGuid(), 0, 63, 0,
            PartyWandCatalog.Version, false, text, null, session.Config.Voice, 1);
        return new(request, false, LabContract.Memory(session), exchanges, liveAudio)
        {
            PersonalityId = PersonalityCatalog.OllivanderId, LivePrompt = snapshot,
            LiveInput = history, InstructionOverride = instructions
        };

        List<object> Input()
        {
            var input = retained.Select(turn => (object)new
            {
                type = "message", role = turn.Role,
                content = new[] { new { type = turn.Role == "assistant" ? "output_text" : "input_text", text = turn.Text } }
            }).ToList();
            input.Insert(0, new { type = "message", role = "developer",
                content = new[] { new { type = "input_text", text = "Synthetic memory context (data): " + LabContract.Memory(session) } } });
            input.Insert(1, new { type = "message", role = "developer", content = new[] { new
            {
                type = "input_text", text = text.Length > 0
                    ? "This is a text-initiated fictional visitor turn. Begin immediately in Czech: answer the final user message once, briefly, then pause and listen. Do not replay or greet the earlier conversation."
                    : "Begin this fictional test interaction immediately in Czech. Greet briefly, ask one question, then pause and listen."
            } } });
            if (text.Length > 0) { input.Add(new { type = "message", role = "user", content = new[] { new { type = "input_text", text } } }); }
            return input;
        }
    }
    private static async Task StartLiveAsync(LabProviderTransport transport, CancellationToken token)
    {
        using var started = await transport.Live!.ReceiveAsync(token);
        if (GptLiveProtocol.RequiredString(started.RootElement, "type", 100) == "error")
        { throw LiveProviderFailure.Read(started.RootElement); }
        if (GptLiveProtocol.RequiredString(started.RootElement, "type", 100) != "session.started"
            || GptLiveProtocol.RequiredString(started.RootElement, "client_event_id", 128) != transport.StartId
            || started.RootElement.GetProperty("session").GetProperty("model").GetString() != GptLiveProtocol.Model)
        { throw new LabException(502, "lab_provider_start_failed"); }
        transport.Live.CaptureSessionId(started.RootElement);
        transport.Live.AudioReady();
    }
    private static async Task SendSilenceAsync(GptLiveConnection connection, CancellationToken token)
    {
        var silence = new byte[9600];
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        while (await timer.WaitForNextTickAsync(token)) { await connection.SendAudioAsync(silence, token); }
    }
    private async Task ReadLiveTurnAsync(LabSession session, GptLiveConnection connection, Stopwatch timer,
        Task uploading, Func<Task> stopInput, CancellationToken token)
    {
        var output = new System.Text.StringBuilder();
        var audioBytes = 0;
        long accountedCost = 0;
        var lastOutputAt = timer.ElapsedMilliseconds;
        var closeId = Guid.NewGuid().ToString("N");
        long? closingAt = null;
        Task<JsonDocument>? pending = null;
        for (var count = 0; count < 4096; count++)
        {
            if (uploading.IsFaulted) { await uploading; }
            if (closingAt is null && output.Length > 0 && timer.ElapsedMilliseconds - lastOutputAt >= 6000)
            {
                await stopInput();
                await connection.CloseAsync(token, closeId);
                closingAt = timer.ElapsedMilliseconds;
            }
            if (closingAt is not null && timer.ElapsedMilliseconds - closingAt > 5000)
            { throw new LabException(502, "lab_provider_close_unconfirmed"); }
            if (output.Length == 0 && timer.ElapsedMilliseconds > 45000)
            { throw new LabException(504, "lab_provider_no_reply"); }
            pending ??= connection.ReceiveAsync(token);
            if (await Task.WhenAny(pending, Task.Delay(100, token)) != pending) { token.ThrowIfCancellationRequested(); continue; }
            using var document = await pending;
            pending = null;
            var value = document.RootElement;
            var type = GptLiveProtocol.RequiredString(value, "type", 100);
            if (type == "session.output_transcript.delta")
            {
                output.Append(GptLiveProtocol.RequiredString(value, "delta", 16000));
                lastOutputAt = timer.ElapsedMilliseconds;
                if (output.Length > 16000) { throw new LabException(409, "lab_transcript_limit"); }
            }
            else if (type == "session.output_audio.delta")
            {
                var bytes = Convert.FromBase64String(GptLiveProtocol.RequiredString(value, "delta", 180_000));
                try
                {
                    audioBytes = checked(audioBytes + bytes.Length);
                    if (bytes.Length == 0 || bytes.Length % 2 != 0 || audioBytes > 24000 * 2 * 190)
                    { throw new InvalidDataException("Invalid Live audio."); }
                    // Live emits continuous output PCM, including silence. Only
                    // speech energy extends the utterance inactivity boundary.
                    if (GptLiveProtocol.HasSpeech(bytes)) { lastOutputAt = timer.ElapsedMilliseconds; }
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            else if (type == "session.delegation.created") { await SimulateDelegationAsync(session, connection, value, token); }
            else if (type == "session.usage.updated") { RecordUsage(session, value, ref accountedCost); }
            else if (type == "session.closed")
            {
                if (closingAt is null || GptLiveProtocol.RequiredString(value, "client_event_id", 128) != closeId
                    || GptLiveProtocol.RequiredString(value, "reason", 100) != "close_requested")
                { throw new LabException(502, "lab_provider_close_unconfirmed"); }
                RecordUsage(session, value, ref accountedCost, required: true);
                SaveEvent(session, LabContract.Event("provider_usage_final", "received"));
                SaveTurn(session, "assistant", output.ToString(), token, timer.ElapsedMilliseconds, audioBytes);
                SaveEvent(session, LabContract.Event("provider_turn", "completed", timer.ElapsedMilliseconds));
                return;
            }
            else if (type == "error") { throw LiveProviderFailure.Read(value); }
        }
        throw new LabException(409, "lab_event_limit");
    }
    private async Task SimulateDelegationAsync(LabSession session, GptLiveConnection connection, JsonElement value, CancellationToken token)
    {
        var delegation = value.GetProperty("delegation");
        var id = GptLiveProtocol.RequiredString(delegation, "id", 200);
        if (GptLiveProtocol.RequiredString(delegation, "target", 20) != "client") { throw new InvalidDataException("Unexpected delegation."); }
        SaveEvent(session, LabContract.Event("tool_requested", "client_delegation"));
        var failed = session.ScenarioId == "tool-failure";
        await connection.SendAsync(new { type = "session.commentary.append", event_id = Guid.NewGuid().ToString("N"),
            delegation_id = id, content = (failed
                ? "Simulated tool execution failure: the fictional tool is unavailable. Explain that no operation succeeded and continue without claiming an outcome. "
                : "Simulation-only tool result: no physical operation dispatched and no production memory read or written. ")
                + "A simulated selection has not been scored; do not invent a confirmed wand or physical outcome. Continue the fictional conversation." }, token);
        SaveEvent(session, LabContract.Event("tool_result", failed ? "simulated_failure" : "simulation_only"));
    }
    private void RecordProposal(LabSession session, RealtimeReply reply)
    {
        if (reply.Decision != "ask")
        {
            SaveEvent(session, LabContract.Event("tool_result", "validated_simulation", decision: reply.Decision,
                compartment: reply.Compartment));
        }
    }
    private void RecordTokens(LabSession session, OpenAiRealtimeConnection connection)
    {
        if (connection.LastInputTokens is { } input && connection.LastOutputTokens is { } output)
        {
            SaveEvent(session, LabContract.Event("provider_tokens", "observed", inputTokens: input, outputTokens: output));
        }
    }

    private void RecordUsage(LabSession session, JsonElement value, ref long accountedCost, bool required = false)
    {
        if (value.TryGetProperty("usage", out var usage) && usage.TryGetProperty("seconds", out var duration)
            && duration.TryGetDouble(out var seconds) && double.IsFinite(seconds) && seconds is >= 0 and <= 190)
        {
            SaveEvent(session, LabContract.Event("provider_usage", "live_seconds", (long)(seconds * 1000)));
            var current = GptLiveProtocol.VoiceCost(seconds);
            if (current < accountedCost) { throw new LabException(502, "lab_provider_usage_regressed"); }
            SaveEvent(session, LabContract.Event("provider_cost", "micro_usd", costMicroUsd: current - accountedCost));
            accountedCost = current;
        }
        else if (required) { throw new LabException(502, "lab_provider_usage_missing"); }
    }
    private LabTurn SaveTurn(LabSession session, string role, string text, CancellationToken executionToken, long? latency = null, int? audio = null,
        string? decision = null, int? compartment = null)
    {
        lock (admissionGate)
        {
            executionToken.ThrowIfCancellationRequested();
            if (stopping) { throw new LabException(503, "lab_runtime_stopping"); }
            LabContract.Text(text, 16000, empty: true);
            var turn = new LabTurn(Guid.NewGuid().ToString("N"), role, text, DateTimeOffset.UtcNow, latency, audio, decision, compartment);
            if (!session.RecordContent && ephemeral.Count >= 200 && !ephemeral.ContainsKey(session.Id))
            { throw new LabException(429, "lab_ephemeral_capacity"); }
            store.Change(session.OwnerId, session.Id, current => current.Status == "open" ? current with
            {
                Turns = [.. current.Turns, session.RecordContent ? turn : turn with { Text = "" }]
            } : throw new LabException(409, "lab_session_closed"));
            if (!session.RecordContent)
            {
                ephemeral.AddOrUpdate(session.Id, [turn], (_, previous) => [.. previous, turn]);
                ephemeralExpiry[session.Id] = DateTimeOffset.UtcNow.AddMinutes(15);
                ephemeralCleanup ??= new Timer(_ => ExpireEphemeral(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            }
            return turn;
        }
    }
    private void SaveEvent(LabSession session, LabEvent entry) =>
        store.Change(session.OwnerId, session.Id, current => current with { Events = [.. current.Events, entry] });

    private void ExpireEphemeral()
    {
        foreach (var entry in ephemeralExpiry.Where(x => x.Value <= DateTimeOffset.UtcNow))
        {
            if (!active.ContainsKey(entry.Key))
            {
                ephemeral.TryRemove(entry.Key, out _);
                ephemeralExpiry.TryRemove(entry.Key, out _);
            }
        }
    }
    public void Dispose()
    {
        lock (admissionGate)
        {
            if (stopping) { return; }
            stopping = true;
            scriptReservations.Clear();
            ephemeralCleanup?.Dispose();
            foreach (var lifetime in active.Values) { lifetime.Cancel(); }
            ephemeral.Clear();
            ephemeralExpiry.Clear();
            // Active calls still execute Release while unwinding. No semaphore
            // wait handle is used, so leave this managed primitive alive for them.
        }
    }

    private sealed class ScriptReservation(Action release) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) { release(); }
        }
    }

    private sealed class LabProviderTransport(GptLiveConnection? live, OpenAiRealtimeConnection? realtime) : IAsyncDisposable
    {
        internal GptLiveConnection? Live { get; } = live;
        internal OpenAiRealtimeConnection? Realtime { get; } = realtime;
        internal string StartId { get; } = Guid.NewGuid().ToString("N");
        public async ValueTask DisposeAsync()
        {
            if (Live is not null) { await Live.DisposeAsync(); }
            if (Realtime is not null) { await Realtime.DisposeAsync(); }
        }
    }
    private sealed class LiveTranscriptCollector(PersonaLabRuntime runtime, LabSession session, LabSocketSender sender,
        CancellationToken executionToken)
    {
        private string? role;
        private string? turnId;
        internal async Task AppendAsync(string currentRole, string delta, CancellationToken token)
        {
            LabTurn? added = null;
            lock (runtime.admissionGate)
            {
                executionToken.ThrowIfCancellationRequested();
                token.ThrowIfCancellationRequested();
                if (role != currentRole)
                {
                    added = runtime.SaveTurn(session, currentRole, delta, executionToken);
                    role = currentRole;
                    turnId = added.Id;
                }
                else
                {
                    runtime.store.Change(session.OwnerId, session.Id, current => current.Status == "open" ? current with
                    {
                        Turns = current.Turns.Select(turn => turn.Id == turnId && session.RecordContent
                            ? turn with { Text = LabContract.Text(turn.Text + delta, 16000, empty: true) } : turn).ToArray()
                    } : throw new LabException(409, "lab_session_closed"));
                    if (!session.RecordContent && runtime.ephemeral.TryGetValue(session.Id, out var turns))
                    {
                        runtime.ephemeral[session.Id] = turns.Select(turn => turn.Id == turnId
                            ? turn with { Text = LabContract.Text(turn.Text + delta, 16000, empty: true) } : turn).ToArray();
                    }
                }
            }
            if (added is not null) { await sender.JsonAsync(new { type = "turn", turn = added }, token); }
            await sender.JsonAsync(new { type = "transcript", role = currentRole, delta }, token);
        }
    }
}

internal sealed class LabSocketSender(WebSocket socket, Func<CancellationToken, Task<bool>>? authorized = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    internal Task JsonAsync(object value, CancellationToken token) => SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, RealtimeProtocol.JsonOptions), WebSocketMessageType.Text, token);
    internal Task AudioAsync(byte[] pcm, CancellationToken token) => SendAsync(pcm, WebSocketMessageType.Binary, token);
    private async Task SendAsync(byte[] bytes, WebSocketMessageType type, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (authorized is not null && !await authorized(token)) { throw new LabException(401, "account_session_revoked"); }
            await socket.SendAsync(bytes, type, true, token);
        }
        finally { gate.Release(); }
    }
}
