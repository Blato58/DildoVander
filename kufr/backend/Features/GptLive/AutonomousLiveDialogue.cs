using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Memory;
using Kufr.Backend.Features.Realtime;

namespace Kufr.Backend.Features.GptLive;

internal sealed record LivePlaybackRequest(Guid PlaybackId, int PcmBytes, long DecisionRevision);
internal sealed record LivePlaybackConfirmation(Guid PlaybackId, int PcmBytes, bool Drained);

internal sealed class LiveCommandTracker(GptLiveConnection connection)
{
    private readonly Dictionary<string, string> commands = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> acknowledged = new(StringComparer.Ordinal);
    internal string? LastAcknowledged { get; private set; }
    internal async Task<string> AppendAsync(string kind, string content, string? delegation, CancellationToken token)
    {
        if (kind is not ("thinking" or "commentary" or "instructions")) throw new ArgumentException("Unsupported append.");
        var id = Guid.NewGuid().ToString("N"); commands.Add(id, "session." + kind + ".appended");
        if (commands.Count > 128) throw new InvalidDataException("Unacknowledged Live context limit.");
        await connection.SendAsync(new { type = "session." + kind + ".append", event_id = id,
            delegation_id = delegation, content = LiveTextBudget.Bound(content) }, token);
        return id;
    }
    internal void Acknowledge(JsonElement value)
    {
        var id = GptLiveProtocol.RequiredString(value, "client_event_id", 128);
        var type = GptLiveProtocol.RequiredString(value, "type", 100);
        if (acknowledged.TryGetValue(id, out var known) && known == type) return;
        if (!commands.Remove(id, out var expected) || expected != type) throw new InvalidDataException("Live context correlation mismatch.");
        acknowledged[id] = type;
        LastAcknowledged = id;
    }
}

internal sealed class AutonomousLiveDialogue(GptLiveConnection connection, WandDecisionService decisions, TimeProvider clock,
    HybridMemoryService? memory = null)
{
    internal async Task<LiveOutcome> RunAsync(RealtimeCall call, Func<byte[], CancellationToken, Task> audio,
        Func<string, CancellationToken, Task> visitor, Func<CancellationToken, Task> pause,
        Func<LivePlaybackRequest, CancellationToken, Task<LivePlaybackConfirmation>> playback,
        Func<(int ReadyMask, int Revision, bool Current)> availability, Func<CancellationToken, Task> stopInput,
        CancellationToken token, MemoryAccess? memoryAccess = null)
    {
        var turn = call.Context;
        var snapshot = turn.LivePrompt ?? throw new InvalidOperationException("Missing frozen Live prompt.");
        var options = snapshot.Options;
        var projection = new LiveConversationProjection(options);
        var tracker = new LiveCommandTracker(connection);
        var startId = Guid.NewGuid().ToString("N");
        await connection.SendAsync(GptLiveProtocol.AutonomousStart(turn, startId), token);
        using (var started = await connection.ReceiveAsync(token))
            if (GptLiveProtocol.RequiredString(started.RootElement, "type", 100) != "session.started"
                || started.RootElement.GetProperty("session").GetProperty("model").GetString() != GptLiveProtocol.Model
                || GptLiveProtocol.RequiredString(started.RootElement, "client_event_id", 128) != startId)
                throw new InvalidDataException("Live startup correlation failed.");
        connection.AudioReady();
        await tracker.AppendAsync("instructions", availability().ReadyMask == 0
            ? "Nová interakce. Žádná fyzická hůlka není dostupná. Oznam tento stav krátce vlastními slovy, bez otázky a bez vymyšleného kusu."
            : "Nová interakce začíná teď. Host ještě nic neřekl. Vytvoř vlastní krátké přivítání, rýpnutí a jednu otevřenou otázku; pak poslouchej.", null, token);
        var startedAt = clock.GetTimestamp();
        TimeSpan Elapsed() => clock.GetElapsedTime(startedAt);
        var tasks = new Dictionary<string, LiveBackendTask>(StringComparer.Ordinal);
        var equivalent = new Dictionary<string, WandDecisionResult>(StringComparer.Ordinal);
        LiveBackendTask? waiting = null;
        TimeSpan waitingSince = default, changedAt = default;
        Task<WandDecisionResult>? work = null;
        Task<MemoryRetrieval>? memoryWork = null;
        string? memoryDelegation = null;
        long memoryVersion = 0;
        Task<JsonDocument>? receive = null;
        WandDecisionInput? workInput = null;
        string? workFingerprint = null;
        string? resultCommand = null;
        bool resultAccepted = false;
        WandDecisionResult? terminal = null;
        var terminalAt = TimeSpan.Zero;
        var terminalSegment = 0;
        var lastSpeech = TimeSpan.Zero;
        var lastOutput = TimeSpan.Zero;
        bool speechSincePause = false, terminalSpeech = false, closing = false;
        var closeAt = TimeSpan.Zero;
        string? closeId = null;
        var sentBytes = 0;
        var providerBytes = 0;
        var paidCalls = 0;
        long cost = 0, reserved = 0;
        double seconds = 0;
        long observedVersion = 0;
        var alternativeCount = 0;
        int? excluded = null;
        var speechBlocked = false;
        Task<LivePlaybackConfirmation>? player = null;
        LivePlaybackRequest? playerRequest = null;
        using var workLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50), clock);
        Task<bool>? tick = null;

        string Key() => projection.Fingerprint + ":" + availability().Revision + ":" + availability().ReadyMask + ":" + excluded;
        bool Valid(WandDecisionResult result) => availability() is { Current: true } a && result.AvailabilityRevision == a.Revision
            && (result.Compartment is null || (a.ReadyMask & (1 << (result.Compartment.Value - 1))) != 0);
        async Task Deliver(WandDecisionResult result, string? delegation)
        {
            if (!Valid(result)) return;
            var text = ResultContext(result);
            if (result.Status != WandDecisionStatus.NeedsInformation)
            {
                terminal = result; terminalAt = Elapsed(); terminalSegment = projection.Segments.Count;
                terminalSpeech = resultAccepted = false; player = null; playerRequest = null;
                await tracker.AppendAsync("instructions", "Následuje platný terminální výsledek. Oznam jej vlastními slovy; nevytvářej další diagnostickou otázku. "
                    + "Návrh dosud nepotvrzuje fyzický pohyb ani prezentaci. Po krátkém zakončení dej prostor případné opravě.", delegation, token);
            }
            resultCommand = await tracker.AppendAsync("commentary", text, delegation, token);
        }
        async Task StartWork(LiveBackendTask task)
        {
            var state = availability();
            if (!state.Current) throw new OperationCanceledException(token);
            var key = Key();
            if (equivalent.TryGetValue(key, out var cached)) { waiting = null; await Deliver(cached, task.DelegationId); return; }
            if (projection.Intent is LiveRequestIntent.Memory or LiveRequestIntent.Remember or LiveRequestIntent.Forget
                && memory is not null && memoryAccess is not null)
            {
                memoryDelegation = task.DelegationId; memoryVersion = projection.ContentVersion;
                memoryWork = projection.Intent == LiveRequestIntent.Memory
                    ? memory.RetrieveAsync(memoryAccess, MemoryScope.Personal, projection.LatestInput, token)
                    : memory.ChangePersonalMemoryAsync(memoryAccess, projection.Intent, new("input:" + projection.Revision, projection.LatestInput), token);
                waiting = null; tasks[task.DelegationId] = task with { State = "retrieving_memory" }; return;
            }
            if (projection.Intent is LiveRequestIntent.StopSpeech or LiveRequestIntent.Repeat or LiveRequestIntent.Technical or LiveRequestIntent.Thanks)
            { waiting = null; tasks[task.DelegationId] = task with { State = "no_backend_work" }; return; }
            if (projection.Intent == LiveRequestIntent.CancelConversation)
            {
                waiting = null;
                await Deliver(new(WandDecisionStatus.Cancelled, null, [], "", projection.Revision, state.Revision), task.DelegationId); return;
            }
            if (projection.Intent == LiveRequestIntent.Alternative)
            {
                if (++alternativeCount > 1) { waiting = null; await Deliver(new(WandDecisionStatus.Cancelled, null, [], "", projection.Revision, state.Revision), task.DelegationId); return; }
            }
            // A device revision change invalidates the existing physical turn
            // correlation. End safely and require a fresh explicit interaction.
            if (state.Revision != turn.Request.DeviceRevision)
            {
                waiting = null; await Deliver(new(WandDecisionStatus.Stale, null, [], "Dostupnost se změnila; je potřeba nový aktuální snapshot.",
                    projection.Revision, state.Revision), task.DelegationId); return;
            }
            workInput = new(projection.Revision, state.ReadyMask, state.Revision, projection.Evidence(), snapshot.Policy,
                projection.AnswerCandidates, projection.DirectSelection, excluded, turn.EveningContext);
            reserved = WandDecisionService.Reservation(workInput);
            if (++paidCalls > 10 || cost + reserved + GptLiveProtocol.VoiceReservation > GptLiveProtocol.SessionBudget)
                throw new InvalidDataException("Live decision budget exhausted.");
            workFingerprint = key; observedVersion = projection.ContentVersion;
            tasks[task.DelegationId] = task with { State = "running", DecisionRevision = projection.Revision, AvailabilityRevision = state.Revision };
            waiting = null;
            var capturedInput = workInput;
            var capturedSegments = projection.Segments.ToArray();
            var capturedEpoch = memory?.StoreEpoch ?? 0;
            work = Task.Run(async () =>
            {
                if (memory is not null && memoryAccess is not null)
                    await memory.PersistContextAsync(memoryAccess, call.SessionGeneration, capturedSegments,
                        capturedInput.Evidence, capturedEpoch, workLifetime.Token);
                return await decisions.DecideAsync(capturedInput, workLifetime.Token);
            }, workLifetime.Token);
        }
        if (availability().ReadyMask == 0)
        {
            terminal = new(WandDecisionStatus.NoAvailableWand, null, [], "", projection.Revision, availability().Revision);
            terminalAt = Elapsed(); terminalSegment = 0; resultAccepted = true;
        }
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested(); receive ??= connection.ReceiveAsync(token); tick ??= timer.WaitForNextTickAsync(token).AsTask();
                var active = new List<Task> { receive, tick };
                if (work is not null) active.Add(work);
                if (memoryWork is not null) active.Add(memoryWork);
                if (player is not null) active.Add(player);
                var winner = await Task.WhenAny(active);
                if (winner == receive)
                {
                    using var document = await receive; receive = null; var value = document.RootElement;
                    switch (GptLiveProtocol.RequiredString(value, "type", 100))
                    {
                        case "session.input_transcript.delta":
                            if (!projection.Append(value, true)) break;
                            changedAt = Elapsed();
                            if (projection.HasCorrection && memory is not null && memoryAccess is not null) memory.InvalidateCurrentFacts(memoryAccess);
                            var fragment = value.GetProperty("delta").GetString()!;
                            foreach (var part in SplitVisitor(fragment)) await visitor(part, token);
                            if (projection.Intent == LiveRequestIntent.CancelConversation)
                            {
                                var pendingTask = tasks.Values.LastOrDefault(t => t.State == "running");
                                if (work is not null)
                                {
                                    // Cancel is not physical rollback. An ambiguous
                                    // paid request retains its conservative reservation.
                                    workLifetime.Cancel(); cost = checked(cost + reserved);
                                    _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.ExecuteSynchronously);
                                    work = null;
                                    if (pendingTask is not null) tasks[pendingTask.DelegationId] = pendingTask with { State = "cancelled" };
                                }
                                waiting = null; player = null; playerRequest = null; speechBlocked = false;
                                await Deliver(new(WandDecisionStatus.Cancelled, null, [], "", projection.Revision,
                                    availability().Revision, OperationState: "proposal_cancelled"), pendingTask?.DelegationId);
                                break;
                            }
                            if (projection.Intent == LiveRequestIntent.StopSpeech)
                            {
                                speechBlocked = true; terminal = null; player = null; playerRequest = null; waiting = null;
                                await tracker.AppendAsync("instructions", "Host chce přestat mluvit. Dej mu prostor a mlč; nevytvářej další otázku. Tento požadavek sám nepotvrzuje zrušení ani dokončení fyzické operace.", null, token);
                            }
                            else if (projection.Intent is not (LiveRequestIntent.Repeat or LiveRequestIntent.Thanks)) speechBlocked = false;
                            if (terminal is not null && projection.Intent is not (LiveRequestIntent.StopSpeech or LiveRequestIntent.Repeat or LiveRequestIntent.Thanks))
                            {
                                excluded = projection.Intent == LiveRequestIntent.Alternative ? terminal.Compartment : excluded;
                                terminal = null; player = null; playerRequest = null;
                                if (closing) throw new InvalidDataException("Terminal speech was interrupted during close.");
                                await tracker.AppendAsync("instructions", "Host dodal novou informaci. Předchozí závěr už není platný; neopakuj jej. Poslouchej, další otázka není automaticky potřeba.", null, token);
                                var last = tasks.Values.LastOrDefault();
                                if (last is not null) { waiting = last with { State = "awaiting_context" }; waitingSince = Elapsed(); }
                            }
                            break;
                        case "session.output_transcript.delta": projection.Append(value, false); lastOutput = Elapsed(); break;
                        case "session.output_audio.delta":
                            var pcm = Convert.FromBase64String(GptLiveProtocol.RequiredString(value, "delta", 180_000));
                            try
                            {
                                providerBytes = checked(providerBytes + pcm.Length);
                                if (pcm.Length == 0 || pcm.Length % 2 != 0 || providerBytes > 24_000 * 2 * 190) throw new InvalidDataException("Invalid Live audio.");
                                if (HasSpeech(pcm, options.SpeechAmplitudeThreshold))
                                { lastSpeech = Elapsed(); speechSincePause = true; if (terminal is not null) terminalSpeech = true; player = null; playerRequest = null; }
                                if (speechSincePause)
                                    for (var offset = 0; offset < pcm.Length; offset += RealtimeProtocol.MaxChunkBytes)
                                    {
                                        var chunk = pcm.AsSpan(offset, Math.Min(RealtimeProtocol.MaxChunkBytes, pcm.Length - offset)).ToArray();
                                        try { await audio(chunk, token); sentBytes += chunk.Length; }
                                        finally { CryptographicOperations.ZeroMemory(chunk); }
                                    }
                            }
                            finally { CryptographicOperations.ZeroMemory(pcm); }
                            break;
                        case "session.delegation.created":
                            var d = value.GetProperty("delegation"); var id = GptLiveProtocol.RequiredString(d, "id", 200);
                            if (GptLiveProtocol.RequiredString(d, "target", 20) != "client") throw new InvalidDataException("Invalid delegation target.");
                            var offsetMs = value.GetProperty("offset_ms").GetInt64();
                            if (offsetMs is < 0 or > 200_000) throw new InvalidDataException("Invalid delegation offset.");
                            if (tasks.ContainsKey(id)) break; // ordinary duplicate is idempotent
                            if (tasks.Count >= 64) throw new InvalidDataException("Delegation limit.");
                            var task = new LiveBackendTask(id, Guid.NewGuid(), call.Correlation.ConversationId, memoryAccess?.Guest?.GuestId,
                                call.SessionGeneration, projection.Revision, availability().Revision, "awaiting_context", call.OperationId, offsetMs);
                            tasks.Add(id, task);
                            if (tasks.Values.Any(t => t.DelegationId != id && t.State is "running" or "retrieving_memory"
                                && t.DecisionRevision == projection.Revision && t.AvailabilityRevision == availability().Revision))
                            { tasks[id] = task with { State = "equivalent_running" }; break; }
                            if (terminal is null && !closing && waiting is null) { waiting = task; waitingSince = Elapsed(); }
                            break;
                        case "session.instructions.appended": case "session.commentary.appended": case "session.thinking.appended":
                            tracker.Acknowledge(value); if (tracker.LastAcknowledged == resultCommand) resultAccepted = true; break;
                        case "session.usage.updated": case "session.closed":
                            var currentSeconds = value.GetProperty("usage").GetProperty("seconds").GetDouble(); _ = GptLiveProtocol.VoiceCost(currentSeconds);
                            if (currentSeconds < seconds) throw new InvalidDataException("Live usage regressed."); seconds = currentSeconds;
                            if (value.GetProperty("type").GetString() == "session.closed")
                            {
                                if (!closing || terminal is null || value.GetProperty("reason").GetString() != "close_requested"
                                    || GptLiveProtocol.RequiredString(value, "client_event_id", 128) != closeId) throw new InvalidDataException("Unexpected Live close.");
                                var actual = ActualTerminal();
                                return new(new(LiveConversationProjection.Tail(projection.LatestInput, 900), actual.Length <= 500 ? actual : actual[^500..],
                                    terminal.Status == WandDecisionStatus.ValidProposal ? "propose" : "no_selection",
                                    LiveTextBudget.Bound(string.Join("; ", terminal.Reasons.Select(r => r.Fact)), 190), terminal.Compartment,
                                    cost + GptLiveProtocol.VoiceCost(seconds)), projection.AnswerCandidates, [])
                                { Autonomous = true, PlaybackVerified = true, Decision = terminal, DecisionRevision = projection.Revision, Segments = projection.Segments.ToArray() };
                            }
                            break;
                        case "info": break;
                        case "error": throw new InvalidDataException("Live rejected a correlated command.");
                        default: throw new InvalidDataException("Unsupported Live event.");
                    }
                    // Always drain queued events before publishing a result. A
                    // transcript gap alone is never a completed user turn.
                    continue;
                }
                if (winner == work)
                {
                    var result = await work!; work = null; cost = checked(cost + result.CostMicroUsd);
                    var task = tasks.Values.LastOrDefault(t => t.State == "running")!;
                    if (observedVersion != projection.ContentVersion || workFingerprint != Key() || !Valid(result))
                    {
                        tasks[task.DelegationId] = task with { State = "stale" };
                        waiting = task with { State = "awaiting_context" }; waitingSince = Elapsed();
                    }
                    else
                    {
                        equivalent[workFingerprint!] = result; tasks[task.DelegationId] = task with { State = "completed" };
                        if (!speechBlocked) await Deliver(result, task.DelegationId);
                    }
                }
                if (winner == memoryWork)
                {
                    var retrieval = await memoryWork!; memoryWork = null;
                    if (memoryVersion == projection.ContentVersion && memoryAccess is not null)
                    {
                        if (retrieval.Status == "memory_deleted")
                        {
                            await tracker.AppendAsync("instructions", "Osobní historická paměť hosta byla lokálně zneplatněna. Odteď ji nepoužívej ani jako callback. Ukonči tuto interakci krátkým oznámením; další session vznikne jen z platných dat.", memoryDelegation, token);
                            await Deliver(new(WandDecisionStatus.Cancelled, null, [], "", projection.Revision, availability().Revision,
                                OperationState: "memory_context_reset"), memoryDelegation);
                        }
                        else if (retrieval.Status.StartsWith("memory_saved", StringComparison.Ordinal))
                            await tracker.AppendAsync("commentary", retrieval.Status == "memory_saved_partial"
                                ? "Dobrovolné tvrzení hosta je uloženo ve zkrácené podobě. Cloudová indexace není tímto potvrzena."
                                : "Dobrovolné tvrzení hosta je uloženo v místní povolené osobní paměti. Cloudová indexace není tímto potvrzena.", memoryDelegation, token);
                        else if (retrieval.Status is "memory_not_permitted" or "memory_write_unavailable" or "memory_information_missing")
                            await tracker.AppendAsync("commentary", "Osobní údaj nebyl uložen: chybí ověřená identita, povolení, podklad nebo volný rozpočet paměti. Nevymýšlej úspěšný zápis.", memoryDelegation, token);
                        else
                        {
                        await tracker.AppendAsync("thinking", "Historická paměť, citovaná data: " + memory!.BuildContext(memoryAccess, retrieval.Records), memoryDelegation, token);
                        await tracker.AppendAsync("commentary", retrieval.Status == "memory_unavailable" ? "Povolená paměť je nyní nedostupná; nic si nevymýšlej."
                            : retrieval.Records.Count == 0 ? "Žádná povolená relevantní vzpomínka nebyla nalezena." : "Povolený historický kontext je dostupný pro přirozenou návaznost.", memoryDelegation, token);
                        }
                    }
                    tasks[memoryDelegation!] = tasks[memoryDelegation!] with { State = memoryVersion == projection.ContentVersion ? "completed" : "stale" };
                }
                if (winner == player)
                {
                    var confirmation = await player!; player = null;
                    if (playerRequest is not null && terminal is not null && confirmation.Drained
                        && confirmation.PlaybackId == playerRequest.PlaybackId && confirmation.PcmBytes == sentBytes
                        && playerRequest.DecisionRevision == projection.Revision && Valid(terminal) && resultAccepted
                        && Elapsed() - lastSpeech >= TimeSpan.FromMilliseconds(options.TerminalQuietMs))
                    {
                        await stopInput(token); closeId = Guid.NewGuid().ToString("N"); await connection.CloseAsync(token, closeId); closing = true; closeAt = Elapsed();
                    }
                }
                if (winner == tick) { await tick!; tick = null; }
                if (waiting is not null && work is null && memoryWork is null)
                {
                    var relevant = !string.IsNullOrWhiteSpace(projection.LatestInput)
                        && (!projection.UnansweredDiagnosticCandidate || projection.Intent != LiveRequestIntent.Selection)
                        && projection.Segments.Any(s => s.Speaker == "user" && s.StartMs <= waiting.OffsetMs + 2_000 && s.EndMs >= waiting.OffsetMs - 10_000);
                    if (relevant && Elapsed() - changedAt >= TimeSpan.FromMilliseconds(options.DelegationContextWaitMs))
                        await StartWork(waiting);
                    else if (Elapsed() - waitingSince >= TimeSpan.FromMilliseconds(options.DelegationTimeoutMs))
                    {
                        tasks[waiting.DelegationId] = waiting with { State = "context_missing" };
                        await tracker.AppendAsync("instructions", "K delegaci chybí spolehlivý přepis odpovědi. Pokud je potřeba výběr, nech krátce zopakovat chybějící část; nic nehádej.", waiting.DelegationId, token);
                        waiting = null;
                    }
                }
                if (speechSincePause && Elapsed() - lastSpeech >= TimeSpan.FromMilliseconds(options.OutputPauseMs))
                { speechSincePause = false; await pause(token); } // callback is a notification, never a player wait
                if (terminal is not null && !Valid(terminal))
                {
                    terminal = null; player = null; playerRequest = null;
                    await tracker.AppendAsync("instructions", "Dostupnost se změnila. Předchozí výběr není platný; neoznamuj jej jako hotový.", null, token);
                    var last = tasks.Values.LastOrDefault();
                    if (last is not null) { waiting = last with { State = "awaiting_context" }; waitingSince = Elapsed(); }
                }
                if (terminal is not null && !closing && player is null && resultAccepted && terminalSpeech
                    && ActualTerminal().Length > 0 && TerminalMentionsResult()
                    && Elapsed() - lastSpeech >= TimeSpan.FromMilliseconds(options.TerminalQuietMs)
                    && Elapsed() - lastOutput >= TimeSpan.FromMilliseconds(options.TerminalQuietMs))
                {
                    playerRequest = new(Guid.NewGuid(), sentBytes, projection.Revision);
                    player = playback(playerRequest, token); // reader stays active during local drain/ACK
                }
                if (closing && Elapsed() - closeAt > TimeSpan.FromSeconds(5)
                    || terminal is not null && !closing && Elapsed() - terminalAt > TimeSpan.FromSeconds(options.TerminalTimeoutSeconds))
                    throw new InvalidDataException("Live playback/finalization unverified.");
            }
        }
        finally
        {
            workLifetime.Cancel();
            if (work is not null) { try { await work; } catch (Exception) { } }
            if (receive is not null) _ = receive.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); else _ = t.Exception; }, TaskContinuationOptions.ExecuteSynchronously);
        }
        string ActualTerminal() => string.Concat(projection.Segments.Skip(terminalSegment).Where(s => s.Speaker == "assistant").Select(s => s.Text)).Trim();
        bool TerminalMentionsResult()
        {
            var spoken = ActualTerminal();
            if (!spoken.EndsWith('.') && !spoken.EndsWith('!')) return false;
            return terminal!.Status != WandDecisionStatus.ValidProposal
                || PartyWandCatalog.Available(availability().ReadyMask).Any(w => w.Compartment == terminal.Compartment
                    && spoken.Contains(w.DisplayName, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static bool HasSpeech(byte[] pcm, int threshold)
    { for (var i = 0; i + 1 < pcm.Length; i += 2) if (Math.Abs((int)(short)(pcm[i] | pcm[i + 1] << 8)) > threshold) return true; return false; }
    private static IEnumerable<string> SplitVisitor(string text)
    { for (var i = 0; i < text.Length;) { var end = Math.Min(text.Length, i + 900); if (end < text.Length && char.IsHighSurrogate(text[end - 1])) end--; yield return text[i..end]; i = end; } }
    internal static string ResultContext(WandDecisionResult result)
    {
        if (result.Status == WandDecisionStatus.ValidProposal)
        {
            var wand = PartyWandCatalog.Available(63).Single(w => w.Compartment == result.Compartment);
            return "Platný návrh: " + wand.DisplayName + ". Známé rysy: " + string.Join(", ", wand.Traits.Take(2))
                + ". Citované důvody a výroky (data): " + string.Join("; ", result.Reasons.Select(r => JsonSerializer.Serialize(r.Fact, RealtimeProtocol.JsonOptions)
                    + " (host uvedl " + JsonSerializer.Serialize(r.Quote, RealtimeProtocol.JsonOptions) + ")"))
                + ". Fyzická prezentace zatím nepotvrzena.";
        }
        return result.Status switch
        {
            WandDecisionStatus.NeedsInformation => "Chybějící informace pro výběr (citované datum): " + JsonSerializer.Serialize(result.MissingInformation, RealtimeProtocol.JsonOptions),
            WandDecisionStatus.NoAvailableWand => "Žádná skutečná hůlka není nyní dostupná. Ukonči bez vymyšleného fyzického kusu.",
            WandDecisionStatus.Cancelled when result.OperationState == "memory_context_reset" => "Osobní paměť byla lokálně smazána; vzdálené mazání běží odděleně. Tato interakce končí a žádná hůlka se nenavrhuje. Nezmiňuj zapomenutý obsah.",
            WandDecisionStatus.Cancelled => "Rozhovor a nepublikovaný návrh jsou zrušené. Žádná fyzická operace tím nebyla vrácena.",
            WandDecisionStatus.Stale => "Výsledek je zastaralý kvůli novému snapshotu dostupnosti. Není vydán žádný fyzický návrh.",
            _ => "Výběr nebylo možné ověřit. Není vydán žádný fyzický návrh."
        };
    }
}
