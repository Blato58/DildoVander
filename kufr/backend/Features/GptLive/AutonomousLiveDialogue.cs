using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Memory;
using Kufr.Backend.Features.Realtime;
using Kufr.Backend.Features.Character;
using Kufr.Backend.Features.Show;

namespace Kufr.Backend.Features.GptLive;

internal sealed record LivePlaybackRequest(Guid PlaybackId, int PcmBytes, long DecisionRevision);
internal sealed record LivePlaybackConfirmation(Guid PlaybackId, int PcmBytes, bool Drained);
internal sealed record OperatorSelectionOverride(int Slot, string Fingerprint, DateTimeOffset ExpiresAt)
{
    internal bool IsCurrent(string fingerprint, DateTimeOffset now) => Fingerprint == fingerprint && now < ExpiresAt;
}

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
    HybridMemoryService? memory = null, ShowCoordinator? show = null)
{
    internal async Task<LiveOutcome> RunAsync(RealtimeCall call, Func<byte[], CancellationToken, Task> audio,
        Func<string, CancellationToken, Task> visitor, Func<CancellationToken, Task> pause,
        Func<LivePlaybackRequest, CancellationToken, Task<LivePlaybackConfirmation>> playback,
        Func<(int ReadyMask, int Revision, bool Current)> availability, Func<CancellationToken, Task> stopInput,
        CancellationToken token, MemoryAccess? memoryAccess = null, Action<double, WandDecisionStatus>? decisionTiming = null,
        Action<string>? terminalState = null,
        Func<long, CancellationToken, Task>? cancelAudio = null,
        IReadOnlyList<string>? recentStyles = null,
        Action<string, bool>? stylePlayed = null,
        Action<double>? directorTiming = null)
    {
        var turn = call.Context;
        var snapshot = turn.LivePrompt ?? throw new InvalidOperationException("Missing frozen Live prompt.");
        var options = snapshot.Options;
        var inputActivity = new LiveInputActivity(clock, options.SpeechAmplitudeThreshold);
        var projection = new LiveConversationProjection(options);
        var character = snapshot.Version == "dv01-adult-v1" ? new CharacterSession(call.SessionGeneration, options.Character, recentStyles) : null;
        using var showVisit = character is null ? null : show?.BeginVisit(call.Correlation.DeviceId, call.EveningId,
            call.Correlation.ConversationId, call.SessionGeneration);
        CharacterDirective? directive = null;
        var deliveredMemory = new HashSet<string>(turn.LiveMemoryReferences, StringComparer.Ordinal);
        var tracker = new LiveCommandTracker(connection);
        var startId = Guid.NewGuid().ToString("N");
        await connection.SendAsync(GptLiveProtocol.AutonomousStart(turn, startId), token);
        using (var started = await connection.ReceiveAsync(token))
            if (GptLiveProtocol.RequiredString(started.RootElement, "type", 100) != "session.started"
                || started.RootElement.GetProperty("session").GetProperty("model").GetString() != GptLiveProtocol.Model
                || GptLiveProtocol.RequiredString(started.RootElement, "client_event_id", 128) != startId)
                throw new InvalidDataException("Live startup correlation failed.");
        connection.ObserveInput(inputActivity.Observe);
        connection.AudioReady();
        if (character is not null)
        {
            directive = CharacterDirector.Evaluate(character, CharacterPhase.Opening, 0);
            await tracker.AppendAsync("instructions", directive.PromptText, null, token);
        }
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
        WandDecisionResult? readyResult = null;
        LiveBackendTask? readyTask = null;
        string? readyFingerprint = null;
        TimeSpan workStartedAt = default;
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
        var alternativeCount = 0;
        int? excluded = null;
        var speechBlocked = false;
        var terminalInputPending = false;
        var announcementPrompted = false;
        Task<LivePlaybackConfirmation>? player = null;
        LivePlaybackRequest? playerRequest = null;
        string? reportedState = null;
        string? directiveCommand = null;
        bool directiveAccepted = true, audioSuppressed = false;
        var lastProviderAudio = TimeSpan.Zero;
        var minimumOutputStart = -1d;
        var lastCharacterContentVersion = -1L;
        var inputRepairs = 0;
        var recoveryAudio = new List<byte[]>();
        var recoveryBytes = 0;
        var recoveryStartedAt = TimeSpan.Zero;
        var recoveryBoundary = false;
        var playedStyles = new HashSet<string>(StringComparer.Ordinal);
        var inputRepairSources = new HashSet<string>(StringComparer.Ordinal);
        bool silencePrompted = false, finishPrompted = false;
        var characterCorrectionPending = false;
        var operatorFinishRequested = false;
        OperatorSelectionOverride? operatorOverride = null;
        var terminalDrained = false;
        using var workLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50), clock);
        Task<bool>? tick = null;

        string Key() => projection.Fingerprint + ":" + availability().Revision + ":" + availability().ReadyMask + ":" + excluded;
        void ClearOverride()
        {
            operatorOverride = null;
            foreach (var key in equivalent.Where(item => item.Value.OperationState == "operator_proposal_only").Select(item => item.Key).ToArray())
            { equivalent.Remove(key); }
        }
        int? CurrentOverride()
        {
            if (operatorOverride is null) { return null; }
            if (operatorOverride.IsCurrent(Key(), clock.GetUtcNow())) { return operatorOverride.Slot; }
            ClearOverride();
            return null;
        }
        bool Valid(WandDecisionResult result) => availability() is { Current: true } a && result.AvailabilityRevision == a.Revision
            && (result.Compartment is null || (a.ReadyMask & (1 << (result.Compartment.Value - 1))) != 0)
            && (result.OperationState != "operator_proposal_only" || CurrentOverride() == result.Compartment);
        void Report(string state)
        {
            if (reportedState == state) return;
            reportedState = state; terminalState?.Invoke(state);
        }
        async Task Direct(CharacterPhase phase)
        {
            if (character is null) { return; }
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            directive = CharacterDirector.Evaluate(character, phase, projection.AnswerCandidates,
                microphoneActive: !closing, inputSpeaking: !inputActivity.QuietFor(options.InputQuietMs));
            if (phase is CharacterPhase.Ready or CharacterPhase.Closing)
            {
                show?.CaptureFinaleAnchors(call.Correlation.DeviceId, call.SessionGeneration,
                    directive.Anchor is { } anchor ? [new FinaleAnchorReference(anchor.Id, anchor.SourceId,
                        anchor.SourceRevision, anchor.EvidenceRevision, anchor.PermissionRevision)] : []);
            }
            directorTiming?.Invoke(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            directiveCommand = await tracker.AppendAsync("instructions", directive.PromptText, null, token);
            directiveAccepted = false;
            if (show is not null && showVisit is not null && directive.PromptText.Length > 0)
            {
                var direction = show.Direction(call.Correlation.DeviceId, call.SessionGeneration, phase,
                    character.Tempo == CharacterTempo.Express, options.Character.FinaleProfile);
                if (direction.Length > 0) { await tracker.AppendAsync("instructions", direction, null, token); }
            }
        }
        async Task CancelResponse()
        {
            if (character is null) { return; }
            ClearOverride();
            show?.CancelScene(call.Correlation.DeviceId, call.SessionGeneration);
            show?.CancelWandRepertoire(call.Correlation.DeviceId, call.SessionGeneration);
            terminalDrained = false;
            character.CancelResponse();
            audioSuppressed = true; minimumOutputStart = projection.LatestInputEnd;
            directiveAccepted = false; directiveCommand = null;
            terminalSegment = projection.Segments.Count;
            recoveryBoundary = false; recoveryStartedAt = Elapsed();
            foreach (var packet in recoveryAudio) { CryptographicOperations.ZeroMemory(packet); }
            recoveryAudio.Clear(); recoveryBytes = 0;
            player = null; playerRequest = null; terminalSpeech = false; speechSincePause = false;
            if (cancelAudio is not null) { await cancelAudio(character.ResponseGeneration, token); }
            Report("character_response_cancelled");
        }
        async Task EnsureShowNotStoppedAsync()
        {
            if (showVisit is null || show is null) { return; }
            if (show.IsCurrentShowVisit(call.Correlation.DeviceId, call.SessionGeneration)) { return; }
            await CancelResponse();
            throw new OperationCanceledException("Current show was stopped or replaced.", token);
        }
        void RecordPlayedStyle()
        {
            show?.ConfirmSpeechProxy(call.Correlation.DeviceId, call.SessionGeneration);
            if (terminal is not null)
            {
                terminalDrained = false;
                show?.OnTerminalAudioStarted(call.Correlation.DeviceId, call.SessionGeneration, terminal.DecisionRevision);
            }
            if (directive is null || directive.StyleId.Length == 0) { return; }
            var key = $"{directive.Phase}:{directive.SourceRevision}:{character!.ResponseGeneration}";
            if (!playedStyles.Add(key)) { return; }
            stylePlayed?.Invoke(directive.StyleId, directive.Phase == CharacterPhase.Opening);
            if (directive.Phase == CharacterPhase.Ready && showVisit is not null)
            { show?.RecordSceneSpeechProxy(call.Correlation.DeviceId, call.SessionGeneration); }
            if (directive.Phase == CharacterPhase.Ready && terminal?.Compartment is { } slot)
            {
                stylePlayed?.Invoke("lore-" + slot, false);
            }
        }
        async Task Deliver(WandDecisionResult result, string? delegation)
        {
            // Operator quick finish uses the existing decision service and all
            // publication gates, without inventing a provider delegation ID.
            if (delegation?.StartsWith("operator:", StringComparison.Ordinal) == true)
            {
                delegation = null;
                if (result.Status == WandDecisionStatus.NeedsInformation)
                { result = result with { Status = WandDecisionStatus.Cancelled, Compartment = null }; }
            }
            if (!Valid(result)) return;
            terminalDrained = false;
            if (character is not null && result.Status != WandDecisionStatus.NeedsInformation)
            {
                show?.BeginFinale(call.Correlation.DeviceId, call.SessionGeneration,
                    character.Tempo == CharacterTempo.Express, options.Character.FinaleProfile,
                    result.Status == WandDecisionStatus.ValidProposal, result.DecisionRevision, result.Compartment);
            }
            waiting = null;
            var text = character is null ? ResultContext(result) : CharacterResultContext(result, options.Character.FinaleProfile);
            await Direct(result.Status == WandDecisionStatus.NeedsInformation ? CharacterPhase.Responding : CharacterPhase.Ready);
            if (character is not null && result.Status == WandDecisionStatus.ValidProposal && result.Compartment is { } slot
                && CharacterLoreProfiles.ForConfirmedSelection(slot, turn.Request.CatalogVersion) is { } lore)
            {
                await tracker.AppendAsync("thinking", "Autorská lore fikce pro potvrzený výběr, nikoli vlastnost či nová identita: "
                    + lore.Premise + " Nikde nevyslovuj název ani slot; lore nemění scoring, původní hlas nebo pohyb.", delegation, token);
                var profile = ShowRepertoire.Wands.Single(wand => wand.SlotId == slot);
                var lines = show?.OfferWandRepertoire(call.Correlation.DeviceId, call.SessionGeneration, slot, result.DecisionRevision)
                    ?? profile.Lines.Where(line => line.Kind == "acceptance").Take(1).ToArray();
                await tracker.AppendAsync("thinking", "Volitelný autorský repertoár hůlky v pořadí úvod, reakce, závěr: "
                    + string.Join(" / ", lines.Select(line => line.Text))
                    + " Styl: " + profile.SpeechStyle + ". Jde o fikci; nevyslovuj název ani slot a netvrď provedený pohyb.", delegation, token);
            }
            if (result.Status != WandDecisionStatus.NeedsInformation)
            {
                terminal = result; terminalAt = Elapsed(); terminalSegment = projection.Segments.Count;
                terminalSpeech = resultAccepted = false; player = null; playerRequest = null;
                terminalInputPending = false;
                announcementPrompted = false;
                Report("result_ready");
                await tracker.AppendAsync("instructions", character is not null
                    ? "Terminální výsledek. Krátká oznamovací ceremonie nebo aktuální callback, bez otázky. "
                        + "Nevyslovuj katalogový název, slot ani nové vlastnosti. Fyzická prezentace není potvrzená. Pak mlč."
                    : "Následuje platný terminální výsledek. Oznam jej vlastními slovy pouze oznamovacími větami. "
                    + "Název hůlky vyslov jako její potvrzený štítek; nic nepřejmenovávej. "
                    + "Výběr proběhne automaticky bez souhlasu hosta. Nepokládej žádnou otázku, ani potvrzovací nebo řečnickou; nevybízej k hodnocení či odpovědi. "
                    + "Po krátkém rýpnutí či callbacku mlč. Návrh dosud nepotvrzuje fyzický pohyb ani prezentaci.", delegation, token);
            }
            resultCommand = await tracker.AppendAsync("commentary", text, delegation, token);
        }
        async Task StartWork(LiveBackendTask task)
        {
            var state = availability();
            if (!state.Current) throw new OperationCanceledException(token);
            var forcedForWork = CurrentOverride();
            var key = Key();
            if (character is not null && projection.LatestIsCharacterControl
                && !operatorFinishRequested
                && CharacterSession.ClassifyExplicitControl(projection.LatestInput) != CharacterSignalKind.RequestExpress)
            { waiting = null; tasks[task.DelegationId] = task with { State = "character_control" }; return; }
            if (projection.Intent is LiveRequestIntent.Memory or LiveRequestIntent.Remember or LiveRequestIntent.Forget
                && character is null && memory is not null && memoryAccess is not null)
            {
                memoryDelegation = task.DelegationId; memoryVersion = projection.ContentVersion;
                memoryWork = projection.Intent == LiveRequestIntent.Memory
                    ? memory.RetrieveAsync(memoryAccess, MemoryScope.Personal, projection.LatestInput, token)
                    : memory.ChangePersonalMemoryAsync(memoryAccess, projection.Intent, new("input:" + projection.Revision, projection.LatestInput), token);
                waiting = null; tasks[task.DelegationId] = task with { State = "retrieving_memory" }; return;
            }
            if (projection.Intent is LiveRequestIntent.StopSpeech or LiveRequestIntent.Repeat or LiveRequestIntent.Technical or LiveRequestIntent.Thanks or LiveRequestIntent.Acknowledge)
            { waiting = null; tasks[task.DelegationId] = task with { State = "no_backend_work" }; return; }
            if (character is null && projection.Intent == LiveRequestIntent.CancelConversation)
            {
                waiting = null;
                await Deliver(new(WandDecisionStatus.Cancelled, null, [], "", projection.Revision, state.Revision), task.DelegationId); return;
            }
            var directSelection = projection.DirectSelection || character?.Tempo == CharacterTempo.Express || operatorFinishRequested;
            if (equivalent.TryGetValue(key, out var cached)
                && !(character is not null && cached.Status == WandDecisionStatus.NeedsInformation
                    && directSelection && snapshot.Policy.AllowEarlySelection))
            { waiting = null; await Deliver(cached with { DecisionRevision = projection.Revision }, task.DelegationId); return; }
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
                projection.AnswerCandidates, directSelection,
                excluded, character is null ? turn.EveningContext : "", character is null ? turn.CostumeContext : "");
            await Direct(CharacterPhase.Deciding);
            reserved = forcedForWork is null ? decisions.Reservation(workInput) : 0;
            if (forcedForWork is null && ++paidCalls > 10)
                throw new InvalidDataException("Live decision count exhausted.");
            workFingerprint = key;
            workStartedAt = Elapsed();
            tasks[task.DelegationId] = task with { State = "running", DecisionRevision = projection.Revision, AvailabilityRevision = state.Revision };
            waiting = null;
            Report("decision_running");
            var capturedInput = workInput;
            var capturedSegments = projection.Segments.ToArray();
            var capturedEpoch = memory?.StoreEpoch ?? 0;
            work = Task.Run(async () =>
            {
                if (character is null && memory is not null && memoryAccess is not null)
                    await memory.PersistContextAsync(memoryAccess, call.SessionGeneration, capturedSegments,
                        capturedInput.Evidence, capturedEpoch, workLifetime.Token);
                return forcedForWork is { } forced
                    ? WandDecisionService.OperatorOverride(capturedInput, forced)
                    : await decisions.DecideAsync(capturedInput, workLifetime.Token);
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
                if (character is not null && showVisit is not null && show is not null)
                {
                    show.ReportSelectionState(call.Correlation.DeviceId, call.SessionGeneration,
                        availability().ReadyMask, terminal is not null || closing, terminal?.Compartment,
                        character.Tone, character.Tempo);
                    foreach (var control in show.TakeControls(call.Correlation.DeviceId, call.SessionGeneration))
                    {
                        switch (control.Kind)
                        {
                            case "override":
                                if (terminal is null && int.TryParse(control.Value, out var forcedSlot)
                                    && forcedSlot is >= 1 and <= 6
                                    && (availability().ReadyMask & (1 << (forcedSlot - 1))) != 0)
                                {
                                    operatorOverride = new(forcedSlot, Key(), control.ExpiresAt);
                                    operatorFinishRequested = true;
                                    equivalent.Clear();
                                    if (readyResult is not null && workInput is not null)
                                    { readyResult = WandDecisionService.OperatorOverride(workInput, forcedSlot); }
                                }
                                break;
                            case "stop":
                                await CancelResponse();
                                throw new OperationCanceledException("Operator ended the current show dialogue.", token);
                            case "guest-leave":
                                showVisit.Leave();
                                await CancelResponse();
                                throw new OperationCanceledException("Guest left the current show dialogue.", token);
                            case "cancel-speech":
                                await CancelResponse();
                                await Direct(CharacterPhase.Listening);
                                break;
                            case "tone":
                                character.ApplyOperatorStyle(tone: control.Value);
                                await CancelResponse();
                                await Direct(CharacterPhase.Responding);
                                break;
                            case "tempo":
                                character.ApplyOperatorStyle(tempo: control.Value);
                                await Direct(CharacterPhase.Responding);
                                break;
                            case "quick-finale":
                                character.ApplyOperatorStyle(tempo: "express");
                                operatorFinishRequested = true;
                                if (terminal is not null && Valid(terminal)) { await Deliver(terminal, null); }
                                break;
                            case "scene-twist":
                                if (terminal is not null)
                                {
                                    show.BeginFinale(call.Correlation.DeviceId, call.SessionGeneration,
                                        character.Tempo == CharacterTempo.Express, options.Character.FinaleProfile,
                                        terminal.Status == WandDecisionStatus.ValidProposal, terminal.DecisionRevision);
                                }
                                await Direct(terminal is null ? CharacterPhase.Responding : CharacterPhase.Ready);
                                break;
                            case "mood":
                                await Direct(terminal is null ? CharacterPhase.Responding : CharacterPhase.Ready);
                                break;
                        }
                    }
                    if (operatorFinishRequested && !closing && terminal is null && work is null && readyResult is null
                        && waiting is null && inputActivity.QuietFor(options.InputQuietMs))
                    {
                        var id = "operator:" + Guid.NewGuid().ToString("N");
                        var task = new LiveBackendTask(id, Guid.NewGuid(), call.Correlation.ConversationId,
                            memoryAccess?.Guest?.GuestId, call.SessionGeneration, projection.Revision,
                            availability().Revision, "awaiting_context", call.OperationId, 0);
                        if (tasks.Count >= 64) { throw new InvalidDataException("Delegation limit."); }
                        tasks.Add(id, task);
                        await StartWork(task);
                        operatorFinishRequested = false;
                    }
                }
                token.ThrowIfCancellationRequested(); receive ??= connection.ReceiveAsync(token); tick ??= timer.WaitForNextTickAsync(token).AsTask();
                var active = new List<Task> { receive, tick };
                if (work is not null) active.Add(work);
                if (memoryWork is not null) active.Add(memoryWork);
                if (player is not null) active.Add(player);
                var winner = await Task.WhenAny(active);
                if (winner == receive)
                {
                    using var document = await receive; receive = null; var value = document.RootElement;
                    if (audioSuppressed && directiveAccepted && inputActivity.QuietFor(options.InputQuietMs)
                        && Elapsed() - lastProviderAudio >= TimeSpan.FromMilliseconds(options.OutputPauseMs))
                    {
                        recoveryBoundary = true;
                    }
                    switch (GptLiveProtocol.RequiredString(value, "type", 100))
                    {
                        case "session.input_transcript.delta":
                            if (!projection.Append(value, true)) break;
                            changedAt = Elapsed();
                            if (character is not null)
                            {
                                characterCorrectionPending |= projection.HasCorrection;
                                if (speechSincePause || terminal is not null && !terminalInputPending) { await CancelResponse(); }
                                if (audioSuppressed) { minimumOutputStart = projection.LatestInputEnd; }
                                silencePrompted = false;
                            }
                            if (character is null && projection.HasCorrection && memory is not null && memoryAccess is not null) memory.InvalidateCurrentFacts(memoryAccess);
                            var fragment = value.GetProperty("delta").GetString()!;
                            foreach (var part in SplitVisitor(fragment)) await visitor(part, token);
                            if (character is null && projection.Intent == LiveRequestIntent.CancelConversation)
                            {
                                var pendingTask = tasks.Values.LastOrDefault(t => t.State is "running" or "awaiting_input_quiet");
                                if (work is not null)
                                {
                                    // Cancel is not physical rollback. An ambiguous
                                    // paid request retains its conservative reservation.
                                    workLifetime.Cancel(); cost = checked(cost + reserved);
                                    _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.ExecuteSynchronously);
                                    work = null;
                                    if (pendingTask is not null) tasks[pendingTask.DelegationId] = pendingTask with { State = "cancelled" };
                                }
                                if (readyTask is not null) tasks[readyTask.DelegationId] = readyTask with { State = "cancelled" };
                                readyResult = null; readyTask = null; readyFingerprint = null;
                                waiting = null; player = null; playerRequest = null; speechBlocked = false;
                                await Deliver(new(WandDecisionStatus.Cancelled, null, [], "", projection.Revision,
                                    availability().Revision, OperationState: "proposal_cancelled"), pendingTask?.DelegationId);
                                break;
                            }
                            if (character is null && projection.Intent == LiveRequestIntent.StopSpeech)
                            {
                                speechBlocked = true; terminal = null; player = null; playerRequest = null; waiting = null;
                                await tracker.AppendAsync("instructions", "Host chce přestat mluvit. Dej mu prostor a mlč; nevytvářej další otázku. Tento požadavek sám nepotvrzuje zrušení ani dokončení fyzické operace.", null, token);
                            }
                            else if (projection.Intent is not (LiveRequestIntent.Repeat or LiveRequestIntent.Thanks)
                                && (character is null || CharacterSession.ClassifyExplicitControl(projection.LatestInput) != CharacterSignalKind.RequestStopSpeech)) speechBlocked = false;
                            if (terminal is not null)
                            {
                                // Input fragments may split even "Dobře". Stop a
                                // pending publication now, but wait for both PCM
                                // quiet and stable text before classifying a new
                                // request or interrupting the guest with context.
                                terminalInputPending = true; player = null; playerRequest = null;
                                if (closing) throw new InvalidDataException("Terminal speech was interrupted during close.");
                            }
                            break;
                        case "session.output_transcript.delta":
                            projection.Append(value, false); lastOutput = Elapsed();
                            if (character is not null && audioSuppressed && directiveAccepted && recoveryBoundary
                                && GptLiveProtocol.Milliseconds(value, "start_ms") >= minimumOutputStart
                                && inputActivity.QuietFor(options.InputQuietMs))
                            {
                                // Live has no response/audio IDs. This acoustic/timeline fence is
                                // a capability-limited recovery, not proof of sample attribution.
                                audioSuppressed = false;
                                terminalSegment = projection.Segments.Count - 1;
                                foreach (var packet in recoveryAudio)
                                {
                                    try
                                    {
                                        await audio(packet, token); sentBytes += packet.Length; RecordPlayedStyle();
                                        speechSincePause = true; if (terminal is not null) { terminalSpeech = true; }
                                    }
                                    finally { CryptographicOperations.ZeroMemory(packet); }
                                }
                                recoveryAudio.Clear(); recoveryBytes = 0;
                            }
                            break;
                        case "session.output_audio.delta":
                            var pcm = Convert.FromBase64String(GptLiveProtocol.RequiredString(value, "delta", 180_000));
                            try
                            {
                                providerBytes = checked(providerBytes + pcm.Length);
                                lastProviderAudio = Elapsed();
                                if (pcm.Length == 0 || pcm.Length % 2 != 0 || providerBytes > 24_000 * 2 * 190) throw new InvalidDataException("Invalid Live audio.");
                                if (audioSuppressed && !recoveryBoundary) { Report("character_audio_suppressed"); }
                                if (audioSuppressed && recoveryBoundary && !speechBlocked)
                                {
                                    if (recoveryBytes + pcm.Length > 96_000) { throw new InvalidDataException("DV01 recovery transcript unavailable."); }
                                    foreach (var offset in Enumerable.Range(0, (pcm.Length + RealtimeProtocol.MaxChunkBytes - 1) / RealtimeProtocol.MaxChunkBytes))
                                    {
                                        var start = offset * RealtimeProtocol.MaxChunkBytes;
                                        var packet = pcm.AsSpan(start, Math.Min(RealtimeProtocol.MaxChunkBytes, pcm.Length - start)).ToArray();
                                        recoveryAudio.Add(packet); recoveryBytes += packet.Length;
                                    }
                                }
                                if (HasSpeech(pcm, options.SpeechAmplitudeThreshold))
                                {
                                    lastSpeech = Elapsed();
                                    if (!audioSuppressed && !speechBlocked)
                                    {
                                        speechSincePause = true; if (terminal is not null) terminalSpeech = true;
                                        player = null; playerRequest = null;
                                    }
                                }
                                if (speechSincePause && !audioSuppressed && !speechBlocked)
                                    for (var offset = 0; offset < pcm.Length; offset += RealtimeProtocol.MaxChunkBytes)
                                    {
                                        var chunk = pcm.AsSpan(offset, Math.Min(RealtimeProtocol.MaxChunkBytes, pcm.Length - offset)).ToArray();
                                        try { await audio(chunk, token); sentBytes += chunk.Length; RecordPlayedStyle(); }
                                        finally { CryptographicOperations.ZeroMemory(chunk); }
                                    }
                            }
                            finally { CryptographicOperations.ZeroMemory(pcm); }
                            break;
                        case "session.delegation.created":
                            var d = value.GetProperty("delegation"); var id = GptLiveProtocol.RequiredString(d, "id", 200);
                            if (GptLiveProtocol.RequiredString(d, "target", 20) != "client") throw new InvalidDataException("Invalid delegation target.");
                            var offsetMs = GptLiveProtocol.Milliseconds(value, "offset_ms");
                            if (tasks.ContainsKey(id)) break; // ordinary duplicate is idempotent
                            if (tasks.Count >= 64) throw new InvalidDataException("Delegation limit.");
                            var task = new LiveBackendTask(id, Guid.NewGuid(), call.Correlation.ConversationId, memoryAccess?.Guest?.GuestId,
                                call.SessionGeneration, projection.Revision, availability().Revision, "awaiting_context", call.OperationId, offsetMs);
                            tasks.Add(id, task);
                            if (tasks.Values.Any(t => t.DelegationId != id && t.State is "running" or "retrieving_memory" or "awaiting_input_quiet"
                                && t.DecisionRevision == projection.Revision && t.AvailabilityRevision == availability().Revision))
                            { tasks[id] = task with { State = "equivalent_running" }; break; }
                            if (terminal is null && !closing && waiting is null) { waiting = task; waitingSince = Elapsed(); }
                            break;
                        case "session.instructions.appended": case "session.commentary.appended": case "session.thinking.appended":
                            tracker.Acknowledge(value);
                            if (tracker.LastAcknowledged == resultCommand) resultAccepted = true;
                            if (tracker.LastAcknowledged == directiveCommand)
                            { directiveAccepted = true; Report("character_directive_accepted"); }
                            break;
                        case "session.usage.updated": case "session.closed":
                            var currentSeconds = value.GetProperty("usage").GetProperty("seconds").GetDouble(); _ = GptLiveProtocol.VoiceCost(currentSeconds);
                            if (currentSeconds < seconds) throw new InvalidDataException("Live usage regressed."); seconds = currentSeconds;
                            if (value.GetProperty("type").GetString() == "session.closed")
                            {
                                if (!closing || terminal is null || value.GetProperty("reason").GetString() != "close_requested"
                                    || GptLiveProtocol.RequiredString(value, "client_event_id", 128) != closeId) throw new InvalidDataException("Unexpected Live close.");
                                await EnsureShowNotStoppedAsync();
                                var actual = ActualTerminal();
                                showVisit?.Complete();
                                return new(new(LiveConversationProjection.Tail(projection.LatestInput, 900), actual.Length <= 500 ? actual : actual[^500..],
                                    terminal.Status == WandDecisionStatus.ValidProposal ? "propose" : "no_selection",
                                    LiveTextBudget.Bound(string.Join("; ", terminal.Reasons.Select(r => r.Fact)), 190), terminal.Compartment,
                                    cost + GptLiveProtocol.VoiceCost(seconds)), projection.AnswerCandidates, [])
                                { Autonomous = true, PlaybackVerified = true, Decision = terminal, DecisionRevision = projection.Revision, Segments = projection.Segments.ToArray() };
                            }
                            break;
                        case "info": break;
                        case "error": throw LiveProviderFailure.Read(value);
                        default: throw new InvalidDataException("Unsupported Live event.");
                    }
                    // Always drain queued events before publishing a result. A
                    // transcript gap alone is never a completed user turn.
                    continue;
                }
                if (winner == work)
                {
                    WandDecisionResult result;
                    try { result = await work!; }
                    catch
                    {
                        decisionTiming?.Invoke((Elapsed() - workStartedAt).TotalMilliseconds, WandDecisionStatus.Error);
                        throw;
                    }
                    finally { work = null; }
                    cost = checked(cost + result.CostMicroUsd);
                    if (CurrentOverride() is { } forcedSlot && workInput is not null)
                    { result = WandDecisionService.OperatorOverride(workInput, forcedSlot); }
                    decisionTiming?.Invoke((Elapsed() - workStartedAt).TotalMilliseconds, result.Status);
                    var task = tasks.Values.LastOrDefault(t => t.State == "running")!;
                    if (workFingerprint != Key() || !Valid(result))
                    {
                        tasks[task.DelegationId] = task with { State = "stale" };
                        waiting = task with { State = "awaiting_context" }; waitingSince = Elapsed();
                    }
                    else
                    {
                        // A guest can resume talking while the model is working,
                        // before its next transcript arrives. Hold the result
                        // instead of forcing commentary over their live audio.
                        readyResult = result; readyTask = task; readyFingerprint = workFingerprint;
                        tasks[task.DelegationId] = task with { State = "awaiting_input_quiet" };
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
                        const string memoryPrefix = "Historická paměť, citovaná data: ";
                        var retrievedText = memory!.BuildContext(memoryAccess, retrieval.Records, deliveredMemory,
                            500 - LiveTextBudget.UpperBound(memoryPrefix));
                        if (retrievedText.Length > 0)
                            await tracker.AppendAsync("thinking", memoryPrefix + retrievedText, memoryDelegation, token);
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
                        && !terminalInputPending
                        && inputActivity.QuietFor(options.InputQuietMs)
                        && Elapsed() - lastSpeech >= TimeSpan.FromMilliseconds(options.TerminalQuietMs))
                    {
                        terminalDrained = true;
                        show?.OnPlaybackConfirmed(call.Correlation.DeviceId, call.SessionGeneration, terminal.DecisionRevision);
                        if (terminal.Compartment is { } playedSlot)
                        {
                            show?.RecordWandSpeechProxy(call.Correlation.DeviceId, call.SessionGeneration,
                                playedSlot, terminal.DecisionRevision, ActualTerminal());
                        }
                    }
                }
                if (winner == tick) { await tick!; tick = null; }
                var sceneProgress = show?.TickScene(call.Correlation.DeviceId, call.SessionGeneration);
                show?.DispatchSceneCues(call.Correlation.DeviceId, call.SessionGeneration);
                sceneProgress = show?.TickScene(call.Correlation.DeviceId, call.SessionGeneration);
                await EnsureShowNotStoppedAsync();
                if (terminalDrained && terminal is not null && !closing && !terminalInputPending && Valid(terminal)
                    && inputActivity.QuietFor(options.InputQuietMs) && sceneProgress?.PausePending != true
                    && sceneProgress?.Cancelled != true
                    && sceneProgress?.Cues.Any(cue => cue.Kind == "PlayLocalClip"
                        && cue.Status is "planned" or "ready_for_adapter" or "submitted" or "accepted") != true)
                {
                    Report("closing");
                    await stopInput(token);
                    await EnsureShowNotStoppedAsync();
                    closeId = Guid.NewGuid().ToString("N");
                    await connection.CloseAsync(token, closeId);
                    closing = true;
                    closeAt = Elapsed();
                }
                if (audioSuppressed && recoveryBytes > 0 && Elapsed() - recoveryStartedAt > TimeSpan.FromSeconds(8))
                {
                    throw new InvalidDataException("DV01 recovery transcript unavailable.");
                }
                if (character is not null && !closing && speechSincePause && !inputActivity.QuietFor(100))
                {
                    await CancelResponse();
                }
                if (character is not null && !closing && projection.ContentVersion != lastCharacterContentVersion
                    && projection.ContentVersion > 0 && inputActivity.QuietFor(options.InputQuietMs)
                    && Elapsed() - changedAt >= TimeSpan.FromMilliseconds(options.DelegationContextWaitMs))
                {
                    lastCharacterContentVersion = projection.ContentVersion;
                    // A transcript delta can end in the middle of a word (Stop+ař).
                    // Semantic voice controls use the same stable/quiet gate as
                    // decision work. Physical/UI Stop remains immediate outside Live.
                    var change = character.AcceptInput(projection.LatestInputReference, projection.ContentVersion,
                        projection.LatestInput, characterCorrectionPending);
                    var control = CharacterSession.ClassifyExplicitControl(projection.LatestInput);
                    if (change.EvidenceChanged && !characterCorrectionPending && control is
                        CharacterSignalKind.AttributeToOther or CharacterSignalKind.CorrectEvidence or CharacterSignalKind.MarkFiction)
                    { projection.InvalidatePriorCharacterEvidence(); }
                    characterCorrectionPending = false;
                    if (control is not null)
                    { projection.MarkLatestCharacterControl(change.EvidenceChanged); }
                    else { projection.ClearLatestCharacterControl(); }
                    if (change.CancelResponse) { await CancelResponse(); }
                    speechBlocked = control == CharacterSignalKind.RequestStopSpeech;
                    if (projection.Intent == LiveRequestIntent.Repeat && inputRepairSources.Add(projection.LatestInputReference)
                        && ++inputRepairs > 2)
                    { throw new InvalidDataException("DV01 input repair limit reached; use existing text/operator path."); }
                    if (change.EndSession)
                    {
                        workLifetime.Cancel();
                        throw new OperationCanceledException("DV01 participation ended.", token);
                    }
                    await Direct(projection.Intent == LiveRequestIntent.Repeat ? CharacterPhase.Repair
                        : terminal is null ? CharacterPhase.Responding : CharacterPhase.Ready);
                }
                if (character is not null && !closing && terminal is null && work is null && readyResult is null && waiting is null
                    && inputActivity.MicrophoneActive && inputActivity.QuietFor(5000)
                    && !speechSincePause && !speechBlocked && projection.UnansweredDiagnosticCandidate
                    && Elapsed() - lastOutput >= TimeSpan.FromSeconds(5) && !silencePrompted)
                {
                    silencePrompted = true;
                    await tracker.AppendAsync("instructions", "Mikrofon je aktivní a čeká se na odpověď. Nejvýše jeden krátký návrat k otázce ve stejném tónu; žádné hodnocení povahy ani automatické změkčení. Potom zjednoduš nebo použij dostupnou cestu bez dalších otázek.", null, token);
                }
                if (character is not null && !finishPrompted && !closing && Elapsed() >= TimeSpan.FromSeconds(90)
                    && inputActivity.QuietFor(options.InputQuietMs) && terminal is null && work is null && readyResult is null)
                {
                    finishPrompted = true;
                    await tracker.AppendAsync("instructions", "Čas preferuje dokončení, ne přerušení hosta. Bez dalších obsahových otázek deleguj aktuální výběr backendu. Pokud evidence nestačí, pravdivě ukonči bez fyzického návrhu; nic náhodně nevybírej.", null, token);
                }
                if (readyResult is not null)
                {
                    if (!inputActivity.QuietFor(options.InputQuietMs)) Report("result_input_active");
                    else if (Elapsed() - changedAt >= TimeSpan.FromMilliseconds(options.DelegationContextWaitMs))
                    {
                        var task = readyTask!;
                        if (readyFingerprint != Key() || !Valid(readyResult))
                        {
                            tasks[task.DelegationId] = task with { State = "stale" };
                            waiting = task with { State = "awaiting_context" }; waitingSince = Elapsed();
                        }
                        else
                        {
                            readyResult = readyResult with { DecisionRevision = projection.Revision };
                            equivalent[readyFingerprint!] = readyResult;
                            tasks[task.DelegationId] = task with { State = "completed" };
                            if (!speechBlocked) await Deliver(readyResult, task.DelegationId);
                        }
                        readyResult = null; readyTask = null; readyFingerprint = null;
                    }
                }
                if (terminal is not null && terminalInputPending && !closing && inputActivity.QuietFor(options.InputQuietMs)
                    && Elapsed() - changedAt >= TimeSpan.FromMilliseconds(options.DelegationContextWaitMs))
                {
                    terminalInputPending = false;
                    if (projection.Intent is LiveRequestIntent.Repeat or LiveRequestIntent.Thanks or LiveRequestIntent.Acknowledge
                        || character is not null && projection.LatestIsCharacterControl
                            && !projection.LatestCharacterControlInvalidatesEvidence)
                    {
                        // An acknowledgment is not new selection evidence. A
                        // split first fragment may have advanced the projection
                        // revision; rebind the unchanged, validated decision.
                        terminal = terminal with { DecisionRevision = projection.Revision };
                        terminalAt = Elapsed();
                    }
                    else
                    {
                        excluded = projection.Intent == LiveRequestIntent.Alternative ? terminal.Compartment : excluded;
                        terminal = null; player = null; playerRequest = null;
                        Report("input_changed");
                        await tracker.AppendAsync("instructions", "Host dodal nové zadání. Předchozí závěr už není platný; neopakuj jej. Další otázka není automaticky potřeba.", null, token);
                        var last = tasks.Values.LastOrDefault();
                        if (last is not null) { waiting = last with { State = "awaiting_context" }; waitingSince = Elapsed(); }
                    }
                }
                if (waiting is not null && work is null && readyResult is null && memoryWork is null)
                {
                    var relevant = !string.IsNullOrWhiteSpace(projection.LatestInput)
                        && (!projection.UnansweredDiagnosticCandidate || projection.Intent != LiveRequestIntent.Selection)
                        && projection.Segments.Any(s => s.Speaker == "user" && s.StartMs <= waiting.OffsetMs + 2_000 && s.EndMs >= waiting.OffsetMs - 10_000);
                    if (relevant && !inputActivity.QuietFor(options.InputQuietMs)) Report("input_active");
                    if (relevant && inputActivity.QuietFor(options.InputQuietMs)
                        && Elapsed() - changedAt >= TimeSpan.FromMilliseconds(options.DelegationContextWaitMs))
                        await StartWork(waiting);
                    else if (!relevant && inputActivity.QuietFor(options.InputQuietMs)
                        && Elapsed() - waitingSince >= TimeSpan.FromMilliseconds(options.DelegationTimeoutMs))
                    {
                        tasks[waiting.DelegationId] = waiting with { State = "context_missing" };
                        if (character is not null && ++inputRepairs > 2)
                        {
                            throw new InvalidDataException("DV01 input repair limit reached; use existing text/operator path.");
                        }
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
                if (character is null && terminal is { Status: WandDecisionStatus.ValidProposal } && !closing && !terminalInputPending
                    && !announcementPrompted && resultAccepted && terminalSpeech && !TerminalMentionsResult()
                    && !LiveTerminalSpeech.Unfinished(ActualTerminal())
                    && inputActivity.QuietFor(options.InputQuietMs)
                    && Elapsed() - lastSpeech >= TimeSpan.FromMilliseconds(Math.Max(2500, options.TerminalQuietMs))
                    && Elapsed() - lastOutput >= TimeSpan.FromMilliseconds(Math.Max(2500, options.TerminalQuietMs)))
                {
                    // One focused repair avoids an entire timeout when Live
                    // omitted or confused the catalog title. Require new spoken
                    // audio and its local drain; the instruction itself is not
                    // completion evidence and does not create another decision.
                    announcementPrompted = true; terminalSpeech = false; terminalSegment = projection.Segments.Count;
                    var name = PartyWandCatalog.Available(availability().ReadyMask).Single(w => w.Compartment == terminal.Compartment).DisplayName;
                    Report("announcement_repair");
                    await tracker.AppendAsync("instructions", "Dokonči verdikt jednou krátkou větou vlastními slovy. Vyslov jen potvrzený název hůlky: "
                        + name + ". Použij oznamovací větu a pak mlč. Nežádej souhlas a nepokládej žádnou otázku, ani řečnickou. Fyzická prezentace zatím není potvrzená.", null, token);
                }
                if (terminal is not null && !closing && !terminalDrained && !terminalInputPending && player is null && resultAccepted && terminalSpeech
                    && ActualTerminal().Length > 0 && TerminalMentionsResult()
                    && inputActivity.QuietFor(options.InputQuietMs)
                    && Elapsed() - lastSpeech >= TimeSpan.FromMilliseconds(options.TerminalQuietMs)
                    && Elapsed() - lastOutput >= TimeSpan.FromMilliseconds(options.TerminalQuietMs))
                {
                    playerRequest = new(Guid.NewGuid(), sentBytes, projection.Revision);
                    Report("awaiting_playback");
                    player = playback(playerRequest, token); // reader stays active during local drain/ACK
                }
                if (terminal is not null && !closing && player is null)
                    Report(terminalInputPending ? "input_pending" : !resultAccepted ? "awaiting_result_ack" : !terminalSpeech ? "awaiting_speech"
                        : !TerminalMentionsResult() ? "awaiting_announcement"
                        : !inputActivity.QuietFor(options.InputQuietMs) ? "input_active" : "output_active");
                if (closing && Elapsed() - closeAt > TimeSpan.FromSeconds(5)
                    || terminal is not null && !closing && !terminalInputPending && inputActivity.QuietFor(options.InputQuietMs)
                        && Elapsed() - terminalAt > TimeSpan.FromSeconds(options.TerminalTimeoutSeconds))
                    throw new InvalidDataException("Live playback/finalization unverified.");
            }
        }
        finally
        {
            character?.Close();
            foreach (var packet in recoveryAudio) { CryptographicOperations.ZeroMemory(packet); }
            connection.ObserveInput(null);
            workLifetime.Cancel();
            if (work is not null) { try { await work; } catch (Exception) { } }
            if (receive is not null) _ = receive.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); else _ = t.Exception; }, TaskContinuationOptions.ExecuteSynchronously);
        }
        string ActualTerminal() => string.Concat(projection.Segments.Skip(terminalSegment).Where(s => s.Speaker == "assistant").Select(s => s.Text)).Trim();
        bool TerminalMentionsResult()
        {
            if (character is not null) { return LimitedTerminalReady(ActualTerminal()); }
            return LiveTerminalSpeech.Ready(ActualTerminal(),
                terminal!.Status == WandDecisionStatus.ValidProposal ? terminal.Compartment : null,
                PartyWandCatalog.Available(63));
        }
    }

    internal static string CharacterResultContext(WandDecisionResult result, FinaleProfile profile)
    {
        if (result.Status != WandDecisionStatus.ValidProposal) { return ResultContext(result); }
        return profile == FinaleProfile.Blocked
            ? "Backend výběr ověřil, ale fyzické finále je v této konfiguraci blokované. Oznam krátkou neosobní provozní opravu, bez názvu a slotu. Žádný reveal nebude vydán."
            : "Backend má platnou strukturovanou volbu. Omezené finále: neuváděj katalogový název, číslo slotu ani faktické vlastnosti. Jeden aktuální callback nebo neosobní ceremonie; mechanika zatím není potvrzená. Hůlka zůstává rekvizitou.";
    }
    internal static bool LimitedTerminalReady(string speech)
    {
        if (LiveTerminalSpeech.Unfinished(speech)) { return false; }
        // This check is after streaming; it blocks proposal publication, never
        // claims to undo words already heard or to validate every semantic fact.
        return !LiveTerminalSpeech.MentionsAnyCatalogTitle(speech, PartyWandCatalog.Available(63))
            && !LiveTerminalSpeech.MentionsCompartmentIdentifier(speech)
            && !System.Text.RegularExpressions.Regex.IsMatch(speech, @"(?i)už.{0,20}(vyjela|vyjel|vysunut)|vezmi.{0,10}(ji|hůlku)");
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
