using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Realtime;
using Microsoft.Extensions.Configuration;

internal static class AutonomousLiveTests
{
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
        await VerifyConversation(options, correction: false);
        await VerifyConversation(options, correction: true);
        await VerifyConversation(options, correction: false, interrupt: true);
        await VerifyConversation(options with { DelegationTimeoutMs = 2000, DelegationContextWaitMs = 350 }, correction: false,
            naturalVerdict: true, visitorStillSpeaking: true, acknowledge: true, resumeDuringDecision: true);
        await VerifyConversation(options, correction: false, naturalVerdict: true, repairVerdict: true);
        await VerifyMissingContext(options);
        await VerifyPlaybackRegistry();
        Console.WriteLine("Autonomous Live: frozen prompts/policy, timed long Czech input, delegation ordering/duplicates, stale correction, actual playback and native wire passed.");
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
        bool resumeDuringDecision = false, bool repairVerdict = false)
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
        socket.Transcript(true, "Plánuji všechno.", 1100, 2000);
        await consumed.Reader.ReadAsync(lifetime.Token);
        Check(calls == 0, "ordinary greeting/question/reaction needs no backend model");
        if (visitorStillSpeaking) await connection.SendAudioAsync(new byte[] { 0, 32, 0, 32 }, lifetime.Token);
        if (!correction)
        {
            socket.Transcript(false, "Jak zvládáš chaos?", 2100, 2300);
            socket.Delegate("d-one", 2500); // newer answer is not transcribed yet
            socket.Transcript(true, "Nevím.", 2400, 2600);
            await consumed.Reader.ReadAsync(lifetime.Token);
        }
        socket.Delegate("d-one", 2500); socket.Delegate("d-one", 2500); // normal duplicate
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
            && outcome.Reply.Reply.Contains("nezdržuj") && outcome.Segments.Any(s => s.Speaker == "user" && s.StartMs == 1100), "actual spoken transcript and timed raw user speech retained separately");
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
        internal void Transcript(bool input, string text, int start, int end) => Emit(new { type = input ? "session.input_transcript.delta" : "session.output_transcript.delta", delta = text, start_ms = start, end_ms = end, event_id = Guid.NewGuid().ToString("N") });
        internal void Delegate(string id, int ms) => Emit(new { type = "session.delegation.created", delegation = new { id, target = "client" }, offset_ms = ms });
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
