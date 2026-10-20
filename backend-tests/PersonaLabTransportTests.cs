using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.PersonaLab;
using Kufr.Backend.Features.Realtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

internal static class PersonaLabTransportTests
{
    internal static async Task RunAsync()
    {
        await RunCaseAsync("text history and privacy", 10, TextHistoryAndPrivacyAsync);
        await RunCaseAsync("eight-turn dialogue recall", 10, DialogueRecallExecutionAsync);
        await RunCaseAsync("provider failure and recovery", 10, ProviderFailureAndRecoveryAsync);
        await RunCaseAsync("suite execution", 10, SuiteExecutionAsync);
        await RunCaseAsync("script isolation between turns", 10, ScriptIsolationBetweenTurnsAsync);
        await RunCaseAsync("Live close and final usage", 30, LiveCompletionAsync);
        await RunCaseAsync("Live provider error categories", 10, LiveErrorCategoriesAsync);
        await RunCaseAsync("observed simulated Live tool failure", 15, LiveToolFailureAsync);
        await RunCaseAsync("shutdown and orphaned suites", 10, ShutdownAndOrphansAsync);
        await RunCaseAsync("voice interruption and reuse", 10, VoiceInterruptionAsync);
        await RunCaseAsync("observed voice recovery", 10, VoiceRecoveryAsync);
        await RunCaseAsync("hardware admission leases", 10, HardwareAdmissionLeasesAsync);
        Console.WriteLine("Persona Lab actual runtime: text history/privacy, provider faults, suite execution, Live final usage, and PCM interruption/reuse passed (fake providers only).");
    }

    private static async Task RunCaseAsync(string name, int timeoutSeconds, Func<Task> test)
    {
        try { await Task.Run(test).WaitAsync(TimeSpan.FromSeconds(timeoutSeconds)); }
        catch (TimeoutException error) { throw new TimeoutException("Persona Lab transport test timed out: " + name, error); }
    }

    private static async Task TextHistoryAndPrivacyAsync()
    {
        using var fixture = new Fixture();
        var session = fixture.Session("returning");
        var first = await fixture.Runtime.TurnAsync("owner", session.Id, "TRANSPORT-FIRST-USER", fixture.Token);
        var second = await fixture.Runtime.TurnAsync("owner", session.Id, "TRANSPORT-SECOND-USER", fixture.Token);
        Check(fixture.RealtimeSockets.Count == 2 && first.Turns.Length == 2 && second.Turns.Length == 4,
            "separate HTTP turns reconnect while preserving both prior turn contents in RAM");
        var secondWire = string.Join('\n', fixture.RealtimeSockets[1].Commands);
        Check(secondWire.Contains("TRANSPORT-FIRST-USER") && secondWire.Contains("TRANSPORT-ANSWER-1")
            && secondWire.Contains("TRANSPORT-SECOND-USER") && StartupMemory(fixture.RealtimeSockets[1].Commands) == LabContract.Memory(session),
            "actual second provider request receives prior user/assistant context and the selected synthetic returning fixture");
        CheckRestoredHistory(fixture.RealtimeSockets[1].Commands,
            [new("TRANSPORT-FIRST-USER", "TRANSPORT-ANSWER-1")], "TRANSPORT-SECOND-USER");
        Check(second.Turns.Where(turn => turn.Role == "assistant").All(turn => turn.LatencyMs >= 15 && turn.AudioBytes == 4800),
            "actual per-request timing and delivered audio byte metadata are recorded");
        var stored = fixture.Store.Session("owner", session.Id);
        Check(stored.Turns.All(turn => turn.Text == "") && stored.Turns.Length == 4,
            "nonrecorded sessions persist metadata with empty content fields");
        var reopened = new PersonaLabStore(fixture.Database, Fixture.Baseline()).Session("owner", session.Id);
        Check(reopened.Turns.All(turn => turn.Text == "") && fixture.Runtime.Detail("owner", session.Id).Turns[0].Text == "TRANSPORT-FIRST-USER",
            "disk reopening exposes metadata only while the active runtime still overlays RAM content");
        var disk = string.Join('\n', Directory.GetFiles(fixture.Directory).Select(path => Encoding.UTF8.GetString(File.ReadAllBytes(path))));
        Check(!disk.Contains("TRANSPORT-FIRST-USER") && !disk.Contains("TRANSPORT-ANSWER-1"),
            "unrecorded input and replies are absent from database and WAL bytes");
        fixture.Runtime.End("owner", session.Id);
        Check(fixture.Runtime.Detail("owner", session.Id).Turns.All(turn => turn.Text == ""), "explicit End clears RAM-only transcript content");
    }

    private static async Task ProviderFailureAndRecoveryAsync()
    {
        using var fixture = new Fixture();
        fixture.FailNextRealtime = true;
        var session = fixture.Session();
        await RejectAsync(() => fixture.Runtime.TurnAsync("owner", session.Id, "UNRECORDED-FAILED-INPUT", fixture.Token));
        var failed = fixture.Runtime.Detail("owner", session.Id);
        Check(fixture.Runtime.ActiveCount == 0 && failed.Events.Any(entry => entry.Type == "provider_failure")
            && failed.Turns.Length == 0, "provider rejection frees runtime admission without fabricating a successful transcript");
        Check(!JsonSerializer.Serialize(failed.Events).Contains("PRIVATE-PROVIDER-DETAIL"), "technical failure metadata excludes provider message contents");
        var recovered = await fixture.Runtime.TurnAsync("owner", session.Id, "RECOVERY-EXPLICIT-NEW-TURN", fixture.Token);
        Check(recovered.Turns.Length == 2 && recovered.Turns[0].Text == "RECOVERY-EXPLICIT-NEW-TURN"
            && fixture.RealtimeSockets.Count == 2, "an explicit later turn can use released capacity without replaying failed input");
    }

    private static async Task DialogueRecallExecutionAsync()
    {
        using var fixture = new Fixture();
        using var host = new TestHostLifetime();
        using var suites = new PersonaLabSuites(fixture.Store, fixture.Runtime, host);
        var started = suites.Start("owner", new(fixture.Revision, RealtimeProtocol.Model, "empty",
            ["dialogue-recall"], "non-cued recall"), _ => Task.FromResult(true));
        var completed = (await WaitSuiteAsync(fixture, started.Id)).Results.Single();
        var saved = fixture.Store.Session("owner", completed.SessionId);
        var script = saved.ScenarioSnapshot!.Script;
        Check(completed.Status == "completed" && completed.TurnObservations.Length == 8
            && fixture.RealtimeSockets.Count == 8 && saved.Status == "ended" && saved.Turns.Length == 16,
            "the eight-turn recall scenario executes and retires every actual fake-provider turn");
        Check(fixture.RealtimeSockets.Select(socket => CurrentInput(socket.Commands)).SequenceEqual(script.Select(step => step.Input)),
            "recall suite executes each immutable input without inserting the expected recall answers");
        var expectedHistory = script.Take(7).Select((step, index) => new DialogExchange(step.Input, $"TRANSPORT-ANSWER-{index + 1}")).ToArray();
        CheckRestoredHistory(fixture.RealtimeSockets[7].Commands, expectedHistory, script[7].Input);
        var anchors = completed.TurnObservations.SelectMany(turn => turn.Assertions)
            .Where(assertion => assertion.Name.StartsWith("anchor:", StringComparison.Ordinal)).ToArray();
        Check(anchors.Length == 7 && anchors.All(assertion => !assertion.Passed && assertion.Status == "failed"),
            "generic fake output fails every non-cued recall anchor instead of manufacturing memory quality");
        Check(saved.Turns.All(turn => turn.Text == "") && completed.TurnObservations.SelectMany(turn => turn.Assertions)
            .Any(assertion => assertion.Name == "memory-provenance-not-exercised" && assertion.Status == "not_exercised"),
            "nonrecorded recall tests persist metadata and never infer verified guest identity from lexical output");
        await suites.StopAsync(fixture.Token);
    }

    private static async Task SuiteExecutionAsync()
    {
        using var fixture = new Fixture();
        using var host = new TestHostLifetime();
        using var suites = new PersonaLabSuites(fixture.Store, fixture.Runtime, host);
        var good = suites.Start("owner", new(fixture.Revision, RealtimeProtocol.Model, "evening", ["first-visit"], "transport suite", Platform: "ios"),
            _ => Task.FromResult(true));
        var result = await WaitSuiteAsync(fixture, good.Id);
        var observed = result.Results.Single();
        var saved = fixture.Store.Session("owner", observed.SessionId);
        var script = saved.ScenarioSnapshot!.Script;
        Check(result.Status == "completed" && observed.TurnObservations.Length == 3
            && observed.TurnObservations.All(turn => turn.Status == "completed" && turn.UserTurnId is not null
                && turn.AssistantTurnId is not null && turn.LatencyMs >= 15 && turn.AudioBytes == 4800),
            "multi-turn suite records actual per-turn transport observations and metadata");
        Check(saved.Status == "ended" && saved.Platform == "ios" && saved.Turns.Length == 6 && saved.Turns.All(turn => turn.Text == ""),
            "multi-turn suite preserves platform, releases session and does not retain transcript without consent");
        Check(observed.ScenarioSha256 == saved.ScenarioSnapshot.ScriptSha256
            && fixture.RealtimeSockets.Take(3).Select(socket => CurrentInput(socket.Commands)).SequenceEqual(script.Select(turn => turn.Input))
            && string.Join('\n', fixture.RealtimeSockets[2].Commands).Contains("TRANSPORT-ANSWER-1", StringComparison.Ordinal),
            "all exact saved Czech inputs execute in order and later provider turns receive earlier response context");
        Check(observed.TurnObservations.Last().Assertions.Any(assertion => assertion.Name == "anchor:orloj" && !assertion.Passed),
            "generic fake answers fail the limited requested anchor check instead of receiving fabricated quality credit");
        var candidate = fixture.Store.CreateRevision(new(fixture.Revision, "Candidate", "Different exact test persona instructions.", RealtimeProtocol.Model, "marin"));
        var other = suites.Start("owner", new(candidate.Id, RealtimeProtocol.Model, "evening", ["first-visit"], "candidate suite", RecordContent: true), _ => Task.FromResult(true));
        var otherResult = (await WaitSuiteAsync(fixture, other.Id)).Results.Single();
        Check(otherResult.ScenarioSha256 == observed.ScenarioSha256
            && fixture.RealtimeSockets.Skip(3).Take(3).Select(socket => CurrentInput(socket.Commands)).SequenceEqual(script.Select(turn => turn.Input))
            && fixture.Store.Session("owner", otherResult.SessionId).Config.Sha256 != saved.Config.Sha256,
            "two persona configurations run identical immutable scripts with separate exact configuration snapshots");
        Check(fixture.Store.Session("owner", otherResult.SessionId).Turns.Where(turn => turn.Role == "user").Select(turn => turn.Text)
            .SequenceEqual(script.Select(turn => turn.Input)), "explicit recording opt-in preserves the actual multi-turn script transcript");
        fixture.FailNextRealtime = true;
        var bad = suites.Start("owner", new(fixture.Revision, RealtimeProtocol.Model, "empty", ["first-visit"], "failed transport suite"),
            _ => Task.FromResult(true));
        var failure = await WaitSuiteAsync(fixture, bad.Id);
        Check(failure.Status == "failed" && failure.Results.Single().Status == "failed"
            && failure.Results[0].TurnObservations.Length == 1
            && failure.Results[0].TurnObservations[0].Assertions.Any(assertion => assertion.Name == "provider-turn" && !assertion.Passed)
            && failure.Results[0].TurnObservations[0].Assertions.Where(assertion => assertion.Name != "provider-turn").All(assertion => assertion.Status == "not_exercised"),
            "failed transport stops the case and leaves unobserved model checks explicitly unexercised");
        var maximumLabel = suites.Start("owner", new(fixture.Revision, RealtimeProtocol.Model, "empty", ["technical-recovery"], new string('L', 80)),
            _ => Task.FromResult(true));
        var bounded = await WaitSuiteAsync(fixture, maximumLabel.Id);
        Check(bounded.Status == "completed" && fixture.Store.Session("owner", bounded.Results.Single().SessionId).Label.Length == 100,
            "maximum valid suite label produces a bounded session label for the longest scenario identifier");
        Check(bounded.Results.Single().TurnObservations.SelectMany(turn => turn.Assertions)
            .Where(assertion => assertion.Name == "network-recovery-not-exercised").All(assertion => assertion.Status == "not_exercised"),
            "scripted network discussion never becomes evidence of actual network recovery");
        try { suites.Start("owner", new(fixture.Revision, RealtimeProtocol.Model, "empty", ["first-visit"], "bad platform", Platform: "android"), _ => Task.FromResult(true)); throw new Exception("Invalid suite platform accepted."); }
        catch (LabException error) when (error.Status == 400) { }
        var socketsBeforeRevocation = fixture.RealtimeSockets.Count;
        var authorizations = 0;
        var revoked = suites.Start("owner", new(fixture.Revision, RealtimeProtocol.Model, "empty", ["first-visit"], "authority withdrawn"),
            _ => Task.FromResult(Interlocked.Increment(ref authorizations) < 3));
        var revokedResult = await WaitSuiteAsync(fixture, revoked.Id);
        Check(revokedResult.Status == "failed" && revokedResult.Results.Single().TurnObservations.Length == 1
            && fixture.RealtimeSockets.Count == socketsBeforeRevocation + 1,
            "authority revoked between script turns preserves the first observation and prevents another provider call");
        await suites.StopAsync(fixture.Token);
    }

    private static async Task<LabSuite> WaitSuiteAsync(Fixture fixture, string id)
    {
        while (true)
        {
            fixture.Token.ThrowIfCancellationRequested();
            var suite = fixture.Store.Suite("owner", id);
            if (suite.Status != "running") { return suite; }
            await Task.Delay(10, fixture.Token);
        }
    }

    private static async Task ScriptIsolationBetweenTurnsAsync()
    {
        using var fixture = new Fixture();
        using var host = new TestHostLifetime();
        using var suites = new PersonaLabSuites(fixture.Store, fixture.Runtime, host);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admissions = 0;
        var suite = suites.Start("owner", new(fixture.Revision, RealtimeProtocol.Model, "empty", ["first-visit"], "exclusive script"),
            async token =>
            {
                if (Interlocked.Increment(ref admissions) == 3)
                {
                    paused.TrySetResult();
                    await resume.Task.WaitAsync(token);
                }
                return true;
            });
        await paused.Task.WaitAsync(fixture.Token);
        var firstResult = fixture.Store.Suite("owner", suite.Id).Results.Single();
        var session = fixture.Runtime.Detail("owner", firstResult.SessionId);
        var before = session.Turns.ToArray();
        Check(firstResult.TurnObservations.Length == 1 && fixture.RealtimeSockets.Count == 1 && !fixture.Runtime.IsActive(session.Id),
            "test pauses after the first persisted script observation while no provider turn is active");
        await ExpectBusyAsync(() => fixture.Runtime.TurnAsync("owner", session.Id, "EXTERNAL-TEXT-MUST-NOT-ENTER", fixture.Token));
        using (var socket = new ClientSocket())
        {
            await ExpectBusyAsync(() => fixture.Runtime.VoiceAsync("owner", session.Id, socket, _ => Task.FromResult(true), fixture.Token));
        }
        ExpectBusy(() => { using var unexpected = fixture.Runtime.ReserveHardware("owner", session.Id); });
        var imageMutation = false;
        ExpectBusy(() => fixture.Runtime.ChangeImage("owner", session.Id, _ => { imageMutation = true; }));
        Check(!imageMutation && fixture.RealtimeSockets.Count == 1
            && fixture.Runtime.Detail("owner", session.Id).Turns.SequenceEqual(before),
            "same-account text, voice, hardware and image attempts cannot mutate script history or open another provider connection");
        resume.TrySetResult();
        var completed = (await WaitSuiteAsync(fixture, suite.Id)).Results.Single();
        Check(completed.Status == "completed" && completed.TurnObservations.Length == session.ScenarioSnapshot!.Script.Length
            && fixture.RealtimeSockets.Select(socket => CurrentInput(socket.Commands)).SequenceEqual(session.ScenarioSnapshot.Script.Select(turn => turn.Input)),
            "the leased runner continues with exactly the saved script after the pause");
        var clearedAfterEnd = false;
        fixture.Runtime.ChangeImage("owner", session.Id, _ => { clearedAfterEnd = true; }, revoke: true);
        Check(clearedAfterEnd, "normal script end releases its reservation before the completed suite is observed");
        fixture.FailNextRealtime = true;
        var failedSuite = suites.Start("owner", new(fixture.Revision, RealtimeProtocol.Model, "empty", ["first-visit"], "failed exclusive script"),
            _ => Task.FromResult(true));
        var failed = (await WaitSuiteAsync(fixture, failedSuite.Id)).Results.Single();
        var clearedAfterFailure = false;
        fixture.Runtime.ChangeImage("owner", failed.SessionId, _ => { clearedAfterFailure = true; }, revoke: true);
        Check(failed.Status == "failed" && clearedAfterFailure, "a terminal provider failure also releases the whole-script reservation");
        var fresh = fixture.Session();
        var freshTurn = await fixture.Runtime.TurnAsync("owner", fresh.Id, "Fresh independent session.", fixture.Token);
        Check(freshTurn.Turns.Any(turn => turn.Role == "assistant") && fixture.Runtime.ActiveCount == 0,
            "completed and failed scripts leave new admissible sessions and shared capacity available");
        await suites.StopAsync(fixture.Token);

        static async Task ExpectBusyAsync(Func<Task> action)
        {
            try { await action(); }
            catch (LabException error) when (error.Status == 409 && error.Code == "lab_session_busy") { return; }
            throw new Exception("Script isolation allowed an external asynchronous mutation.");
        }
        static void ExpectBusy(Action action)
        {
            try { action(); }
            catch (LabException error) when (error.Status == 409 && error.Code == "lab_session_busy") { return; }
            throw new Exception("Script isolation allowed an external synchronous mutation.");
        }
    }

    private static async Task LiveCompletionAsync()
    {
        using var fixture = new Fixture();
        fixture.HoldNextLiveClose = true;
        var session = fixture.Session("returning", GptLiveProtocol.Model);
        var first = fixture.Runtime.TurnAsync("owner", session.Id, "LIVE-FIRST-USER", fixture.Token);
        await fixture.LiveConnected.Reader.ReadAsync(fixture.Token);
        var firstSocket = fixture.LiveSockets[0];
        await firstSocket.CloseRequested.Task.WaitAsync(fixture.Token);
        Check(!first.IsCompleted && !fixture.Runtime.Detail("owner", session.Id).Events.Any(entry => entry.Type == "provider_usage_final"),
            "Live quiet output and session.close request cannot substitute for final usage acknowledgement");
        firstSocket.AcknowledgeClose();
        var completed = await first;
        Check(firstSocket.SilentOutputChunks > 1 && firstSocket.ClosedAt < TimeSpan.FromSeconds(12),
            "continuous silent output PCM does not keep the text turn active until its technical timeout");
        Check(firstSocket.AudioChunks >= 2 && firstSocket.AudioTimes[1] - firstSocket.AudioTimes[0] >= TimeSpan.FromMilliseconds(150),
            "Live text turns advance with paced PCM silence instead of a burst or an idle timeline");
        Check(completed.Turns.Last().Text == "LIVE-ANSWER-1" && completed.Events.Any(entry => entry.Type == "provider_usage_final"),
            "actual Live turn completes only after correlated closed and final usage");
        Check(completed.Events.Where(entry => entry.Type == "provider_cost").Sum(entry => entry.CostMicroUsd ?? 0) == GptLiveProtocol.VoiceCost(1.25),
            "incremental and final Live usage count only their cost difference");
        await fixture.Runtime.TurnAsync("owner", session.Id, "LIVE-SECOND-USER", fixture.Token);
        using var start = JsonDocument.Parse(fixture.LiveSockets[1].Commands.First(command => command.Contains("session.start")));
        var inputs = start.RootElement.GetProperty("session").GetProperty("input").EnumerateArray().ToArray();
        var context = string.Join('\n', inputs.SelectMany(input => input.GetProperty("content").EnumerateArray()
            .Select(part => part.GetProperty("text").GetString())));
        Check(inputs.Select(input => input.GetProperty("role").GetString()).SequenceEqual(["developer", "developer", "user", "assistant", "user"])
            && context.Contains("LIVE-FIRST-USER") && context.Contains("LIVE-ANSWER-1") && context.Contains("LIVE-SECOND-USER")
            && context.Contains(LabContract.Memory(session)), "new Live provider receives exact prior user/assistant turns, current input and memory fixture");
        fixture.InvalidNextLiveUsage = true;
        await RejectAsync(() => fixture.Runtime.TurnAsync("owner", session.Id, "LIVE-MISSING-USAGE", fixture.Token));
        var failed = fixture.Runtime.Detail("owner", session.Id);
        Check(failed.Turns.Count(turn => turn.Role == "assistant") == 2 && failed.Events.Count(entry => entry.Type == "provider_usage_final") == 2,
            "correlated provider close without final usage cannot publish a successful assistant turn or final usage receipt");
    }

    private static async Task LiveErrorCategoriesAsync()
    {
        using var fixture = new Fixture();
        foreach (var (stage, expected) in new[]
        {
            ("startup", "lab_provider_rejected_command"), ("audio", "lab_provider_unknown_parameter")
        })
        {
            fixture.NextLiveError = stage;
            var session = fixture.Session(engine: GptLiveProtocol.Model);
            try
            {
                await fixture.Runtime.TurnAsync("owner", session.Id, "A valid fictional test input.", fixture.Token);
                throw new Exception("Live provider error accepted.");
            }
            catch (LabException error) when (error.Code == expected) { }
            var socket = fixture.LiveSockets.Last();
            var uploaded = socket.AudioChunks;
            await Task.Delay(250, fixture.Token);
            Check(socket.AudioChunks == uploaded, "failed Live turn joins its PCM uploader before returning");
            var saved = fixture.Store.Session("owner", session.Id);
            Check(saved.Events.Any(entry => entry.Type == "provider_failure" && "lab_" + entry.Code == expected)
                && !JsonSerializer.Serialize(saved).Contains("PRIVATE-PROVIDER-PAYLOAD", StringComparison.Ordinal),
                "startup and active provider errors preserve fixed categories without raw provider messages");
        }
    }

    private static async Task LiveToolFailureAsync()
    {
        using var fixture = new Fixture();
        fixture.NextLiveError = "delegate";
        var session = fixture.Session(engine: GptLiveProtocol.Model, scenario: "tool-failure");
        var result = await fixture.Runtime.TurnAsync("owner", session.Id, session.ScenarioSnapshot!.Script[0].Input, fixture.Token);
        var assertion = LabRubric.InspectTurn(session.ScenarioSnapshot.Script[0], result.Turns.Last(turn => turn.Role == "assistant"), result.Events)
            .Single(check => check.Name == "tool-failure-observed");
        Check(assertion.Status == "passed" && result.Events.Any(entry => entry.Type == "tool_requested" && entry.Code == "client_delegation")
            && result.Events.Any(entry => entry.Type == "tool_result" && entry.Code == "simulated_failure"),
            "real Live client delegation receives and records the simulated failure before a narrow exercised assertion can pass");
        using var commentary = JsonDocument.Parse(fixture.LiveSockets.Single().Commands.Single(command =>
            command.Contains("\"session.commentary.append\"", StringComparison.Ordinal)));
        Check(commentary.RootElement.GetProperty("content").GetString()!.Contains("no operation succeeded", StringComparison.Ordinal)
            && result.Events.Where(entry => entry.Type == "tool_result").All(entry => entry.Compartment is null && entry.Decision is null),
            "actual delegated failure reports no successful operation and never creates a confirmed compartment proposal");
        var unrequested = LabRubric.InspectTurn(session.ScenarioSnapshot.Script[0], result.Turns.Last(turn => turn.Role == "assistant"),
            [LabContract.Event("tool_result", "validated_simulation")]).Single(check => check.Name == "tool-failure-observed");
        Check(unrequested.Status == "not_exercised", "ordinary validated Realtime tool simulation is not proof of an injected tool failure");
    }

    private static async Task ShutdownAndOrphansAsync()
    {
        using (var fixture = new Fixture())
        {
            fixture.NextLiveError = "hold-startup";
            var session = fixture.Session(engine: GptLiveProtocol.Model);
            var pending = fixture.Runtime.TurnAsync("owner", session.Id, "Blocked provider startup.", fixture.Token);
            await fixture.LiveConnected.Reader.ReadAsync(fixture.Token);
            fixture.Runtime.Dispose();
            try { await pending; throw new Exception("Disposed runtime accepted a pending provider call."); }
            catch (LabException error) when (error.Status is 408 or 503) { }
            Check(fixture.Runtime.ActiveCount == 0, "runtime shutdown cancels and unwinds pending work without disposing its release semaphore");
            try { await fixture.Runtime.TurnAsync("owner", session.Id, "After shutdown.", fixture.Token); throw new Exception("Stopped runtime admitted work."); }
            catch (LabException error) when (error.Code == "lab_runtime_stopping") { }
        }
        using (var fixture = new Fixture())
        using (var host = new TestHostLifetime())
        using (var suites = new PersonaLabSuites(fixture.Store, fixture.Runtime, host))
        {
            var orphan = fixture.Store.CreateSuite("owner", new(fixture.Revision, GptLiveProtocol.Model, "empty", ["first-visit"], "orphan"));
            var finished = fixture.Session();
            fixture.Store.SaveSuite(orphan with { Results = [new("first-visit", finished.Id, "completed", [])] });
            var reopened = new PersonaLabStore(fixture.Database, Fixture.Baseline());
            using var restarted = new PersonaLabSuites(reopened, fixture.Runtime, host);
            await restarted.StartAsync(fixture.Token);
            var recovered = reopened.Suite("owner", orphan.Id);
            Check(recovered.Status == "failed" && recovered.FailureCode == "interrupted" && recovered.CompletedAt is not null
                && recovered.Results.Single().SessionId == finished.Id && fixture.LiveSockets.Count == 0,
                "startup makes an orphaned suite terminal, preserves finished results and never replays paid work");
            await restarted.StopAsync(fixture.Token);
            fixture.NextLiveError = "hold-startup";
            var running = suites.Start("owner", new(fixture.Revision, GptLiveProtocol.Model, "empty", ["first-visit"], "shutdown"), _ => Task.FromResult(true));
            await fixture.LiveConnected.Reader.ReadAsync(fixture.Token);
            await suites.StopAsync(fixture.Token);
            var stopped = fixture.Store.Suite("owner", running.Id);
            Check(stopped.Status == "failed" && stopped.FailureCode == "interrupted" && fixture.Runtime.ActiveCount == 0,
                "hosted suite shutdown joins its pending provider call and stores an interrupted terminal result");
            try { suites.Start("owner", new(fixture.Revision, GptLiveProtocol.Model, "empty", ["stop"], "after stop"), _ => Task.FromResult(true)); throw new Exception("Stopped suites admitted work."); }
            catch (LabException error) when (error.Code == "lab_suites_stopping") { }
        }
    }

    private static async Task VoiceRecoveryAsync()
    {
        using var fixture = new Fixture();
        fixture.FailNextRealtime = true;
        var session = fixture.Session();
        using (var failedSocket = new ClientSocket())
        {
            var failure = fixture.Runtime.VoiceAsync("owner", session.Id, failedSocket, _ => Task.FromResult(true), fixture.Token);
            await failedSocket.JsonAsync("ready", fixture.Token);
            failedSocket.PushBinary(new byte[4800]);
            try { await failure; throw new Exception("Voice fault accepted."); }
            catch (LabException error) when (error.Status == 502) { }
        }
        fixture.VoiceNextRealtime = true;
        using var socket = new ClientSocket();
        var retry = fixture.Runtime.VoiceAsync("owner", session.Id, socket, _ => Task.FromResult(true), fixture.Token, recovery: true);
        await socket.JsonAsync("ready", fixture.Token);
        socket.PushBinary(new byte[4800]);
        await socket.BinaryAsync(fixture.Token);
        socket.Push(new { type = "end" });
        await retry;
        var events = fixture.Store.Session("owner", session.Id).Events;
        Check(events.Count(item => item.Type == "recovery_started") == 1 && events.Count(item => item.Type == "recovery_completed") == 1,
            "explicit voice retry records one recovery attempt and success only after verified provider readiness");
        await fixture.Runtime.TurnAsync("owner", session.Id, "A fresh HTTP text connection is not voice recovery.", fixture.Token);
        Check(fixture.Store.Session("owner", session.Id).Events.Count(item => item.Type == "recovery_started") == 1,
            "ordinary new HTTP provider connections never fabricate recovery attempts");
    }

    private static async Task HardwareAdmissionLeasesAsync()
    {
        using (var fixture = new Fixture(new() { MaxConcurrentSessions = 2 }))
        {
            var first = fixture.Session();
            var second = fixture.Session();
            var third = fixture.Session();
            using var firstLease = fixture.Runtime.ReserveHardware("owner", first.Id);
            Check(fixture.Runtime.ActiveCount == 1 && !firstLease.Token.IsCancellationRequested
                && firstLease.Session.Id == first.Id && firstLease.ExpiresAt > DateTimeOffset.UtcNow,
                "hardware admission owns one active operation and a bounded cancellation lifetime");
            await RejectAdmissionAsync(() => fixture.Runtime.TurnAsync("owner", first.Id, "Competing text.", fixture.Token),
                409, "lab_session_busy");
            using (var socket = new ClientSocket())
            {
                await RejectAdmissionAsync(() => fixture.Runtime.VoiceAsync("owner", first.Id, socket, _ => Task.FromResult(true), fixture.Token),
                    409, "lab_session_busy");
            }
            ExpectLeaseFailure(() => fixture.Runtime.ReserveHardware("owner", first.Id), 409, "lab_session_busy");
            using var oldSecond = fixture.Runtime.ReserveHardware("owner", second.Id);
            await RejectAdmissionAsync(() => fixture.Runtime.TurnAsync("owner", third.Id, "Global capacity.", fixture.Token),
                429, "lab_concurrency_limit");
            using (var socket = new ClientSocket())
            {
                await RejectAdmissionAsync(() => fixture.Runtime.VoiceAsync("owner", third.Id, socket, _ => Task.FromResult(true), fixture.Token),
                    429, "lab_concurrency_limit");
            }
            ExpectLeaseFailure(() => fixture.Runtime.ReserveHardware("owner", third.Id), 429, "lab_concurrency_limit");
            Check(fixture.RealtimeSockets.Count == 0 && fixture.LiveSockets.Count == 0,
                "competing provider operations are rejected before any connection is opened");
            oldSecond.Dispose();
            using var newerSecond = fixture.Runtime.ReserveHardware("owner", second.Id);
            oldSecond.Dispose();
            Check(fixture.Runtime.ActiveCount == 2 && !newerSecond.Token.IsCancellationRequested,
                "disposing an old lease twice cannot remove or cancel a newer operation on the same session");
            ExpectLeaseFailure(() => fixture.Runtime.ReserveHardware("owner", third.Id), 429, "lab_concurrency_limit");
            fixture.Runtime.End("owner", first.Id);
            Check(firstLease.Token.IsCancellationRequested && fixture.Runtime.ActiveCount == 2,
                "ending a session cancels its hardware lease while admission remains held until disposal");
            firstLease.Dispose();
            firstLease.Dispose();
            using var thirdLease = fixture.Runtime.ReserveHardware("owner", third.Id);
            Check(fixture.Runtime.ActiveCount == 2 && !thirdLease.Token.IsCancellationRequested,
                "a disposed lease releases exactly one slot for another session");
            var fourth = fixture.Session();
            ExpectLeaseFailure(() => fixture.Runtime.ReserveHardware("owner", fourth.Id), 429, "lab_concurrency_limit");
            newerSecond.Dispose();
            thirdLease.Dispose();
            Check(fixture.Runtime.ActiveCount == 0, "all hardware admission slots are released without any hardware broker");
        }
        using (var unavailable = new Fixture(new() { MaxConcurrentSessions = 1 }, liveAvailable: false))
        {
            var session = unavailable.Session(engine: GptLiveProtocol.Model);
            ExpectLeaseFailure(() => unavailable.Runtime.ReserveHardware("owner", session.Id), 503, "lab_provider_unavailable");
            Check(unavailable.Runtime.ActiveCount == 0 && unavailable.LiveSockets.Count == 0,
                "an explicitly unavailable provider cannot reserve hardware admission or open a provider socket");
            using var availableLease = unavailable.Runtime.ReserveHardware("owner", unavailable.Session().Id);
            Check(unavailable.Runtime.ActiveCount == 1, "unavailable-provider rejection leaves the single shared capacity slot available");
        }

        static async Task RejectAdmissionAsync(Func<Task> action, int status, string code)
        {
            try { await action(); }
            catch (LabException error) when (error.Status == status && error.Code == code) { return; }
            throw new Exception("Persona Lab hardware admission accepted a competing operation: " + code);
        }
        static void ExpectLeaseFailure(Func<LabHardwareLease> action, int status, string code)
        {
            try { using var unexpected = action(); }
            catch (LabException error) when (error.Status == status && error.Code == code) { return; }
            throw new Exception("Persona Lab hardware admission accepted an invalid lease: " + code);
        }
    }

    private static async Task VoiceInterruptionAsync()
    {
        using var fixture = new Fixture();
        fixture.VoiceNextRealtime = true;
        var session = fixture.Session();
        using var client = new ClientSocket();
        var voice = fixture.Runtime.VoiceAsync("owner", session.Id, client, _ => Task.FromResult(true), fixture.Token);
        await fixture.RealtimeConnected.Reader.ReadAsync(fixture.Token);
        var provider = fixture.RealtimeSockets[0];
        await provider.Configurations.Reader.ReadAsync(fixture.Token);
        await client.JsonAsync("ready", fixture.Token);
        client.PushBinary(new byte[4800]);
        await client.BinaryAsync(fixture.Token);
        client.Push(new { type = "cancel", playedAudioMs = 50 });
        await client.JsonAsync("interrupted", fixture.Token);
        Check(provider.CancelCommands == 1 && provider.TruncatedAtMs is >= 0 and <= 50,
            "raw voice cancel uses actual provider response cancellation and bounded playback truncation");
        await provider.Configurations.Reader.ReadAsync(fixture.Token);
        client.PushBinary(new byte[4800]);
        var reply = await client.JsonAsync("turn", fixture.Token, root => root.GetProperty("turn").GetProperty("role").GetString() == "assistant");
        Check(reply.GetProperty("turn").GetProperty("text").GetString() == "TRANSPORT-ANSWER-1"
            && fixture.RealtimeSockets.Count == 1 && provider.AudioUploads == 2,
            "after interruption raw PCM and a new assistant reply use the same provider socket");
        client.Push(new { type = "end" });
        await voice.WaitAsync(fixture.Token);
        Check(fixture.Runtime.ActiveCount == 0 && fixture.Store.Session("owner", session.Id).Turns.All(turn => turn.Text == ""),
            "voice end releases runtime resources and persists only metadata without consent");
    }

    private static async Task RejectAsync(Func<Task<LabSession>> action)
    {
        try { await action(); }
        catch (LabException error) when (error.Status == 502) { return; }
        throw new Exception("Persona Lab transport: provider failure was accepted.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new Exception("Persona Lab transport: " + message); }
    }

    private static void CheckRestoredHistory(IReadOnlyList<string> commands, IReadOnlyList<DialogExchange> expected, string currentInput)
    {
        var inputs = new List<(int Index, string Text)>();
        var responseIndex = -1;
        for (var index = 0; index < commands.Count; index++)
        {
            using var document = JsonDocument.Parse(commands[index]);
            var root = document.RootElement;
            if (root.GetProperty("type").GetString() == "response.create") { responseIndex = index; break; }
            if (!root.TryGetProperty("item", out var item) || !item.TryGetProperty("role", out var role)
                || role.GetString() != "user" || !item.TryGetProperty("content", out var content)) { continue; }
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text) && text.GetString() is { } value) { inputs.Add((index, value)); }
            }
        }
        var restored = inputs.Single(input => input.Text.Contains("\"eveningMemory\"", StringComparison.Ordinal));
        using var context = JsonDocument.Parse(restored.Text[restored.Text.IndexOf('{')..]);
        var history = context.RootElement.GetProperty("history").EnumerateArray().Select(exchange => new DialogExchange(
            exchange.GetProperty("visitorText").GetString()!, exchange.GetProperty("reply").GetString()!)).ToArray();
        Check(history.SequenceEqual(expected), "decoded provider history contains exactly the ordered prior visitor/assistant pairs");
        Check(inputs.Count == 2 && inputs[1].Text == currentInput && restored.Index < inputs[1].Index
            && inputs[1].Index < responseIndex, "restored history and distinct current input precede response creation on the wire");
    }

    private static string? StartupMemory(IEnumerable<string> commands)
    {
        foreach (var command in commands)
        {
            using var document = JsonDocument.Parse(command);
            if (!document.RootElement.TryGetProperty("item", out var item) || !item.TryGetProperty("content", out var content)) { continue; }
            foreach (var part in content.EnumerateArray())
            {
                if (!part.TryGetProperty("text", out var value) || value.GetString() is not { } text
                    || !text.Contains("\"eveningMemory\"", StringComparison.Ordinal)) { continue; }
                using var context = JsonDocument.Parse(text[text.IndexOf('{')..]);
                return context.RootElement.GetProperty("eveningMemory").GetString();
            }
        }
        return null;
    }
    private static string? CurrentInput(IEnumerable<string> commands)
    {
        string? current = null;
        foreach (var command in commands)
        {
            using var document = JsonDocument.Parse(command);
            if (!document.RootElement.TryGetProperty("item", out var item)
                || !item.TryGetProperty("role", out var role) || role.GetString() != "user"
                || !item.TryGetProperty("content", out var content)) { continue; }
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var value) && value.GetString() is { } text
                    && !text.Contains("\"eveningMemory\"", StringComparison.Ordinal)) { current = text; }
            }
        }
        return current;
    }

    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "persona-lab-transport-" + Guid.NewGuid().ToString("N"));
        internal string Database => Path.Combine(Directory, "lab.sqlite");
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(45));
        private readonly GptLiveClient live;
        private readonly OpenAiRealtimeClient realtime;
        private readonly PersonaLabImages images = new(TimeProvider.System);
        internal CancellationToken Token => lifetime.Token;
        internal PersonaLabStore Store { get; }
        internal PersonaLabRuntime Runtime { get; }
        internal string Revision { get; }
        internal List<RealtimeSocket> RealtimeSockets { get; } = [];
        internal List<LiveSocket> LiveSockets { get; } = [];
        internal Channel<bool> RealtimeConnected { get; } = Channel.CreateUnbounded<bool>();
        internal Channel<bool> LiveConnected { get; } = Channel.CreateUnbounded<bool>();
        internal bool FailNextRealtime { get; set; }
        internal bool VoiceNextRealtime { get; set; }
        internal bool HoldNextLiveClose { get; set; }
        internal bool InvalidNextLiveUsage { get; set; }
        internal string? NextLiveError { get; set; }
        internal static LivePromptSnapshot Baseline() => LivePromptSnapshot.Create(PersonalityCatalog.OllivanderId, new GptLiveOptions());

        internal Fixture(PersonaLabOptions? options = null, bool liveAvailable = true)
        {
            Store = new(Database, Baseline());
            Revision = Store.CreateRevision(new(Store.ActiveRevisionId, "Transport fixture", "Exact transport persona instructions.", RealtimeProtocol.Model, "marin")).Id;
            realtime = new(_ =>
            {
                var socket = new RealtimeSocket("TRANSPORT-ANSWER-" + (RealtimeSockets.Count + 1), FailNextRealtime, VoiceNextRealtime);
                FailNextRealtime = false; VoiceNextRealtime = false;
                RealtimeSockets.Add(socket); RealtimeConnected.Writer.TryWrite(true);
                return Task.FromResult<WebSocket>(socket);
            });
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["OPENAI_API_KEY"] = liveAvailable ? "fixture-key-not-a-credential" : null }).Build();
            live = new(configuration, _ =>
            {
                var socket = new LiveSocket("LIVE-ANSWER-" + (LiveSockets.Count + 1), HoldNextLiveClose, InvalidNextLiveUsage, NextLiveError);
                HoldNextLiveClose = false; InvalidNextLiveUsage = false;
                NextLiveError = null;
                LiveSockets.Add(socket); LiveConnected.Writer.TryWrite(true);
                return Task.FromResult<WebSocket>(socket);
            });
            Runtime = new(Store, live, realtime, images, options);
        }

        internal LabSession Session(string fixture = "empty", string engine = RealtimeProtocol.Model, string scenario = "first-visit") =>
            Store.CreateSession("owner", new(Revision, engine, scenario, fixture, "transport fixture"));
        public void Dispose()
        {
            lifetime.Cancel();
            Runtime.Dispose();
            foreach (var socket in RealtimeSockets) { socket.Dispose(); }
            foreach (var socket in LiveSockets) { socket.Dispose(); }
            realtime.Dispose(); live.Dispose(); images.Dispose(); lifetime.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private abstract class QueuedSocket : WebSocket
    {
        private readonly Channel<(byte[] Data, WebSocketMessageType Type)> incoming = Channel.CreateUnbounded<(byte[], WebSocketMessageType)>();
        private readonly CancellationTokenSource aborted = new();
        private (byte[] Data, WebSocketMessageType Type)? fragment;
        private int offset;
        internal List<string> Commands { get; } = [];
        internal void Push(object value) => incoming.Writer.TryWrite((JsonSerializer.SerializeToUtf8Bytes(value), WebSocketMessageType.Text));
        internal void PushBinary(byte[] pcm) => incoming.Writer.TryWrite((pcm, WebSocketMessageType.Binary));
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => aborted.IsCancellationRequested ? WebSocketState.Aborted : WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() => aborted.Cancel();
        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, aborted.Token);
            fragment ??= await incoming.Reader.ReadAsync(linked.Token);
            var value = fragment.Value;
            var count = Math.Min(buffer.Count, value.Data.Length - offset);
            value.Data.AsSpan(offset, count).CopyTo(buffer.AsSpan());
            offset += count;
            var complete = offset == value.Data.Length;
            if (complete) { fragment = null; offset = 0; }
            return new(count, value.Type, complete);
        }
    }

    private sealed class RealtimeSocket(string reply, bool fail, bool voice) : QueuedSocket
    {
        internal Channel<bool> Configurations { get; } = Channel.CreateUnbounded<bool>();
        internal int Responses { get; private set; }
        internal int AudioUploads { get; private set; }
        internal int CancelCommands { get; private set; }
        internal int? TruncatedAtMs { get; private set; }
        private bool created;
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool complete, CancellationToken token)
        {
            using var message = JsonDocument.Parse(buffer);
            var root = message.RootElement;
            Commands.Add(root.GetRawText());
            switch (root.GetProperty("type").GetString())
            {
                case "session.update":
                    Push(new { type = "session.updated", session = new { type = "realtime", model = RealtimeProtocol.Model } });
                    Configurations.Writer.TryWrite(true);
                    break;
                case "input_audio_buffer.append":
                    AudioUploads++;
                    Push(new { type = "input_audio_buffer.speech_started", item_id = "visitor-" + AudioUploads, audio_start_ms = 0 });
                    Push(new { type = "input_audio_buffer.speech_stopped", item_id = "visitor-" + AudioUploads, audio_end_ms = 100 });
                    Push(new { type = "input_audio_buffer.committed", item_id = "visitor-" + AudioUploads });
                    Push(new { type = "conversation.item.input_audio_transcription.completed", item_id = "visitor-" + AudioUploads, transcript = "Fictional raw microphone input." });
                    break;
                case "response.create":
                    Responses++;
                    if (fail)
                    {
                        Push(new { type = "error", error = new { type = "server_error", code = "fixture_failure", message = "PRIVATE-PROVIDER-DETAIL" } });
                        break;
                    }
                    Push(new { type = "response.created", response = new { id = "response-" + Responses } });
                    Push(new { type = "response.output_audio.delta", response_id = "response-" + Responses, item_id = "assistant-" + Responses,
                        content_index = 0, delta = Convert.ToBase64String(new byte[4800]) });
                    await Task.Delay(20, token);
                    if (!voice || Responses > 1) { Done("completed"); }
                    break;
                case "response.cancel":
                    CancelCommands++;
                    Done("cancelled");
                    break;
                case "conversation.item.truncate":
                    TruncatedAtMs = root.GetProperty("audio_end_ms").GetInt32();
                    break;
            }
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            if (!created)
            {
                created = true;
                Push(new { type = "session.created", session = new { type = "realtime", model = RealtimeProtocol.Model } });
            }
            return base.ReceiveAsync(buffer, token);
        }
        private void Done(string status) => Push(new { type = "response.done", response = new
        {
            id = "response-" + Responses, status, output = new[] { new { type = "message", role = "assistant", status = "completed",
                content = new[] { new { type = "output_audio", transcript = reply } } } },
            usage = new { input_tokens = 2, output_tokens = 2, input_token_details = new { text_tokens = 2, audio_tokens = 0 } }
        } });
    }

    private sealed class LiveSocket(string reply, bool holdClose, bool invalidUsage, string? errorStage = null) : QueuedSocket
    {
        internal TaskCompletionSource CloseRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? closeId;
        private bool spoken;
        private bool closed;
        private readonly System.Diagnostics.Stopwatch timeline = System.Diagnostics.Stopwatch.StartNew();
        internal int AudioChunks { get; private set; }
        internal int SilentOutputChunks { get; private set; }
        internal TimeSpan ClosedAt { get; private set; }
        internal List<TimeSpan> AudioTimes { get; } = [];
        internal void AcknowledgeClose()
        {
            if (invalidUsage) { Push(new { type = "session.closed", client_event_id = closeId, reason = "close_requested" }); }
            else { Push(new { type = "session.closed", client_event_id = closeId, reason = "close_requested", usage = new { seconds = 1.25 } }); }
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool complete, CancellationToken token)
        {
            using var message = JsonDocument.Parse(buffer);
            var root = message.RootElement;
            Commands.Add(root.GetRawText());
            switch (root.GetProperty("type").GetString())
            {
                case "session.start":
                    var input = root.GetProperty("session").GetProperty("input").EnumerateArray().ToArray();
                    Check(input.Any(item => item.GetProperty("role").GetString() == "developer"
                        && item.GetProperty("content")[0].GetProperty("text").GetString()!.Contains("Begin immediately", StringComparison.Ordinal)),
                        "text reply trigger is available in the startup developer context");
                    foreach (var item in input)
                    {
                        Check(item.GetProperty("content")[0].GetProperty("type").GetString()
                            == (item.GetProperty("role").GetString() == "assistant" ? "output_text" : "input_text"),
                            "Live startup uses documented text content type for each message role");
                    }
                    if (errorStage == "hold-startup") { break; }
                    if (errorStage == "startup")
                    { Push(new { type = "error", error = new { type = "invalid_request_error", code = "unrecognized-provider-code", message = "PRIVATE-PROVIDER-PAYLOAD" } }); }
                    else { Push(new { type = "session.started", client_event_id = root.GetProperty("event_id").GetString(), session = new { model = GptLiveProtocol.Model, id = "live-" + reply } }); }
                    break;
                case "session.instructions.append":
                    Check(root.TryGetProperty("delegation_id", out var delegation) && delegation.ValueKind == JsonValueKind.Null,
                        "standalone Live append explicitly carries a null delegation reference");
                    break;
                case "session.commentary.append" when errorStage == "delegate":
                    Check(root.GetProperty("delegation_id").GetString() == "fixture-tool-failure", "simulated tool failure is correlated to the actual requested delegation");
                    Push(new { type = "session.output_audio.delta", delta = "AAA=" });
                    Push(new { type = "session.output_transcript.delta", delta = reply });
                    break;
                case "session.input_audio.append":
                    Check(!closed, "Live text input stops and joins before session.close");
                    var pcm = Convert.FromBase64String(root.GetProperty("audio").GetString()!);
                    Check(pcm.Length == 9600 && pcm.All(value => value == 0), "Live text input is 200ms PCM24k mono16 silence");
                    AudioChunks++;
                    AudioTimes.Add(timeline.Elapsed);
                    if (spoken)
                    {
                        SilentOutputChunks++;
                        Push(new { type = "session.output_audio.delta", delta = Convert.ToBase64String(new byte[9600]) });
                        break;
                    }
                    spoken = true;
                    if (errorStage == "audio")
                    {
                        Push(new { type = "error", error = new { type = "invalid_request_error", code = "unknown_parameter", message = "PRIVATE-PROVIDER-PAYLOAD" } });
                        break;
                    }
                    if (errorStage == "delegate")
                    {
                        Push(new { type = "session.delegation.created", delegation = new { id = "fixture-tool-failure", target = "client" } });
                        break;
                    }
                    Push(new { type = "session.usage.updated", usage = new { seconds = 0.5 } });
                    Push(new { type = "session.output_audio.delta", delta = "AAA=" });
                    Push(new { type = "session.output_transcript.delta", delta = reply });
                    break;
                case "session.close":
                    closed = true;
                    ClosedAt = timeline.Elapsed;
                    closeId = root.GetProperty("event_id").GetString();
                    CloseRequested.TrySetResult();
                    if (!holdClose) { AcknowledgeClose(); }
                    break;
            }
            return Task.CompletedTask;
        }
    }

    private sealed class ClientSocket : QueuedSocket
    {
        private readonly Channel<(byte[] Data, WebSocketMessageType Type)> output = Channel.CreateUnbounded<(byte[], WebSocketMessageType)>();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool complete, CancellationToken token)
        {
            output.Writer.TryWrite((buffer.ToArray(), type));
            return Task.CompletedTask;
        }
        internal async Task<JsonElement> JsonAsync(string type, CancellationToken token, Func<JsonElement, bool>? predicate = null)
        {
            while (true)
            {
                var frame = await output.Reader.ReadAsync(token);
                if (frame.Type != WebSocketMessageType.Text) { continue; }
                using var document = JsonDocument.Parse(frame.Data);
                var root = document.RootElement;
                if (root.GetProperty("type").GetString() == type && (predicate is null || predicate(root))) { return root.Clone(); }
            }
        }
        internal async Task BinaryAsync(CancellationToken token)
        {
            while ((await output.Reader.ReadAsync(token)).Type != WebSocketMessageType.Binary) { }
        }
    }

    private sealed class TestHostLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => stopping.Cancel();
        public void Dispose() { stopping.Cancel(); stopping.Dispose(); }
    }
}
