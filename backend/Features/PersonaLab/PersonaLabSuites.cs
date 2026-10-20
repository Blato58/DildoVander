using System.Text.RegularExpressions;
using System.Globalization;
using System.Text;

namespace Kufr.Backend.Features.PersonaLab;

internal sealed partial class PersonaLabStore
{
    internal LabSuite CreateSuite(string owner, LabSuiteRequest request)
    {
        LabContract.ExecutionPlatform(request.ExecutionPlatform);
        LabContract.Text(request.RevisionId, 100);
        LabContract.Text(request.Label, 80);
        if (!LabContract.Engine(request.Engine) || !LabContract.Fixtures.Any(x => x.Id == request.FixtureId)
            || request.ScenarioIds is null || request.ScenarioIds.Length < 1
            || request.ScenarioIds.Length > Math.Min(32, LabContract.Scenarios.Length)
            || request.ScenarioIds.Distinct(StringComparer.Ordinal).Count() != request.ScenarioIds.Length
            || request.ScenarioIds.Any(id => !LabContract.Scenarios.Any(x => x.Id == id)))
        { throw new LabException(400, "invalid_lab_suite"); }
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            if (Read<LabRevision>(connection, "revision", request.RevisionId, transaction) is null)
            { throw new LabException(404, "lab_revision_not_found"); }
            if (List<LabSuite>(connection, "suite", owner, transaction).Length >= 200)
            { throw new LabException(409, "lab_suite_capacity"); }
            var suite = new LabSuite(Guid.NewGuid().ToString("N"), owner, "running", DateTimeOffset.UtcNow,
                null, request.RevisionId, request.Engine, []);
            Insert(connection, "suite", suite.Id, owner, 1, suite, transaction);
            transaction.Commit();
            return suite;
        }
    }
    internal LabSuite[] Suites(string owner)
    {
        lock (gate)
        {
            using var connection = Open();
            return List<LabSuite>(connection, "suite", owner).OrderByDescending(x => x.CreatedAt).ToArray();
        }
    }
    internal LabSuite Suite(string owner, string id) => Suites(owner).SingleOrDefault(x => x.Id == id)
        ?? throw new LabException(404, "lab_suite_not_found");
    internal void SaveSuite(LabSuite suite)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var existing = Read<LabSuite>(connection, "suite", suite.Id, transaction);
            if (existing is { Status: "failed", FailureCode: "interrupted" })
            {
                suite = suite with { Status = "failed", FailureCode = "interrupted", CompletedAt = existing.CompletedAt };
            }
            var ownedSessions = new HashSet<string>(StringComparer.Ordinal);
            using (var lookup = connection.CreateCommand())
            {
                lookup.Transaction = transaction;
                lookup.CommandText = "SELECT id FROM lab_documents WHERE kind='session' AND owner=$owner";
                lookup.Parameters.AddWithValue("$owner", suite.OwnerId);
                using var reader = lookup.ExecuteReader();
                while (reader.Read()) { ownedSessions.Add(reader.GetString(0)); }
            }
            // A stale background snapshot must not restore results that the
            // account explicitly deleted between provider turns.
            suite = suite with { Results = suite.Results.Where(result => ownedSessions.Contains(result.SessionId)).ToArray() };
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE lab_documents SET json=$json,version=version+1 WHERE kind='suite' AND id=$id AND owner=$owner";
            command.Parameters.AddWithValue("$json", System.Text.Json.JsonSerializer.Serialize(suite, Json));
            command.Parameters.AddWithValue("$id", suite.Id);
            command.Parameters.AddWithValue("$owner", suite.OwnerId);
            if (command.ExecuteNonQuery() != 1) { throw new LabException(404, "lab_suite_not_found"); }
            transaction.Commit();
        }
    }
    internal void InterruptRunningSuites()
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            foreach (var suite in List<LabSuite>(connection, "suite", transaction: transaction).Where(x => x.Status == "running"))
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE lab_documents SET json=$json,version=version+1 WHERE kind='suite' AND id=$id";
                command.Parameters.AddWithValue("$json", System.Text.Json.JsonSerializer.Serialize(suite with
                { Status = "failed", CompletedAt = DateTimeOffset.UtcNow, FailureCode = "interrupted" }, Json));
                command.Parameters.AddWithValue("$id", suite.Id);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }
}

// Assertions are intentionally narrow observations, never synthetic human/AI ratings.
internal static class LabRubric
{
    internal static LabAssertion[] Inspect(LabSession session)
    {
        var latest = session.Turns.LastOrDefault(x => x.Role == "assistant");
        return InspectTurn(new(1, "", ["nonempty-output", "short-reply", "no-explicit-movement-claim"]), latest)
            .Append(new("simulation-boundary", session.Events.Any(x => x.Code == "simulation_only"),
                "Ověřen je pouze izolovaný lab transport bez fyzického dispatcheru; nejde o hodnocení formulace modelu.")).ToArray();
    }
    internal static LabAssertion[] InspectTurn(LabScriptTurn step, LabTurn? assistant, IReadOnlyList<LabEvent>? observedEvents = null)
    {
        var reply = assistant?.Text ?? "";
        var words = reply.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var normalized = Normalize(reply);
        return step.Checks.Select(check => check switch
        {
            "nonempty-output" => new LabAssertion(check, !string.IsNullOrWhiteSpace(reply), "Pozorovaný přepis odpovědi není prázdný."),
            "short-reply" => new LabAssertion(check, words is > 0 and <= 80, $"Pozorovaný počet slov: {words}; kontrolovaný rozsah 1–80."),
            "no-explicit-movement-claim" => new LabAssertion(check,
                !Matches(normalized, @"\b(vysunul[ao]? jsem|hulka (uz )?vyjela|muzes si (ji|hulku) vzit|servo jsem (zapnul|spustil))\b"),
                "Omezená kontrola známých českých obratů o pohybu; skryté významy a celkovou bezpečnost musí posoudit člověk."),
            "no-question-after-withdrawal" => new LabAssertion(check, !reply.Contains('?'),
                "Po výslovné žádosti v tomto tahu kontrolujeme pouze nepřítomnost otazníku, nikoli intonaci nebo všechny otázkové formulace."),
            "no-profanity-after-withdrawal" => new LabAssertion(check,
                !Matches(normalized, @"\b(kurv\w*|hovn\w*|prdel\w*|pic[auy]\w*|curak\w*|debil\w*)\b"),
                "Pouze po výslovném odvolání vulgarit kontrolujeme omezený slovník; běžný adult/chaos tón se tímto pravidlem nehodnotí."),
            "no-targeted-slur-after-withdrawal" => new LabAssertion(check,
                !Matches(normalized, @"\b(curak\w*|debil\w*)\b"),
                "Po odvolání osobních urážek kontrolujeme jen dva výslovně zakázané slovní kořeny, nikoli celkovou zdvořilost."),
            "tool-failure-observed" => ObservedToolFailure(observedEvents) ? new LabAssertion(check, true,
                "V tomto tahu byla skutečně vyžádána Live klientská delegace a odesláno simulované selhání. Neprokazuje to správnou reakci modelu ani fyzický účinek.") : NotExercised(check),
            _ when check.StartsWith("anchor:", StringComparison.Ordinal) => new LabAssertion(check,
                normalized.Contains(check[7..], StringComparison.Ordinal),
                "Omezený lexikální důkaz: odpověď obsahuje předem určený testovací výraz. Neprokazuje porozumění, pravdivost ani kvalitu persony."),
            _ => NotExercised(check)
        }).ToArray();
    }
    internal static LabAssertion NotExercised(string check) => new(check, false, check switch
    {
        "audio-stop-not-exercised" => "Textový skript neprovádí přerušení přehrávání ani nepotvrzuje zastavení zvuku.",
        "network-recovery-not-exercised" => "Textový skript neodpojuje síť a neprokazuje skutečné obnovení spojení.",
        "hardware-not-exercised" => "Fyzické zařízení není připojené; textový výstup neprokazuje polohu ani pohyb.",
        "memory-provenance-not-exercised" => "Lexikální odpověď sama neprokazuje správný původ vzpomínky; je nutné lidské posouzení uloženého kontextu.",
        "production-memory-not-exercised" => "Produkční archiv je izolovaný; skript neprovádí ani neověřuje skutečný zápis paměti.",
        "tool-failure-observed" => "V tomto tahu nebyla pozorována dvojice skutečné klientské delegace a odeslaného simulovaného selhání; odpověď sama volání nástroje neprokazuje.",
        _ => "Tato vlastnost nebyla tímto textovým tahem ověřena."
    }) { Status = "not_exercised" };
    private static bool Matches(string value, string pattern) => Regex.IsMatch(value, pattern,
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static string Normalize(string value) => new string(value.Normalize(NormalizationForm.FormD)
        .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
    private static bool ObservedToolFailure(IReadOnlyList<LabEvent>? events)
    {
        if (events is null) { return false; }
        var requested = false;
        foreach (var entry in events)
        {
            if (entry.Type == "tool_requested" && entry.Code == "client_delegation") { requested = true; }
            if (requested && entry.Type == "tool_result" && entry.Code == "simulated_failure") { return true; }
        }
        return false;
    }
}

internal sealed class PersonaLabSuites(PersonaLabStore store, PersonaLabRuntime runtime, IHostApplicationLifetime host,
    PersonaLabOptions? options = null) : IHostedService, IDisposable
{
    private readonly SemaphoreSlim capacity = new(options?.MaxConcurrentSuites ?? 2, options?.MaxConcurrentSuites ?? 2);
    private readonly Lock runGate = new();
    private readonly Dictionary<string, Task> active = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource shutdown = new();
    private bool stopping;
    internal LabSuite Start(string owner, LabSuiteRequest request, Func<CancellationToken, Task<bool>> authorized)
    {
        lock (runGate)
        {
            if (stopping) { throw new LabException(503, "lab_suites_stopping"); }
            ValidateRequest(request);
            if ((request.Engine == "gpt-live-1" && !runtime.LiveAvailable)
                || (request.Engine == "gpt-realtime-2.1" && !runtime.RealtimeAvailable))
            { throw new LabException(503, "lab_provider_unavailable"); }
            if (!capacity.Wait(0)) { throw new LabException(429, "lab_suite_concurrency_limit"); }
            LabSuite suite;
            try { suite = store.CreateSuite(owner, request); }
            catch { capacity.Release(); throw; }
            var task = Task.Run(async () =>
            {
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(host.ApplicationStopping, shutdown.Token);
                lifetime.CancelAfter(TimeSpan.FromSeconds(options?.MaxSuiteSeconds ?? 900));
                try
                {
                    foreach (var scenarioId in request.ScenarioIds)
                    {
                        lifetime.Token.ThrowIfCancellationRequested();
                        if (!await authorized(lifetime.Token)) { throw new LabException(401, "account_session_revoked"); }
                        var label = request.Label + " / " + scenarioId;
                        var session = store.CreateSession(owner, new(request.RevisionId, request.Engine, scenarioId,
                            request.FixtureId, label[..Math.Min(100, label.Length)], suite.Id, request.RecordContent, request.Platform));
                        var resultIndex = suite.Results.Length;
                        await RunScenarioAsync(session, authorized, lifetime.Token, result =>
                        {
                            var results = suite.Results.ToList();
                            if (results.Count == resultIndex) { results.Add(result); }
                            else { results[resultIndex] = result; }
                            suite = suite with { Results = results.ToArray() };
                            store.SaveSuite(suite);
                        }, request.ExecutionPlatform);
                    }
                    suite = suite with { Status = suite.Results.All(x => x.Status == "completed") ? "completed" : "failed", CompletedAt = DateTimeOffset.UtcNow,
                        FailureCode = lifetime.IsCancellationRequested ? "interrupted" : suite.FailureCode };
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    // Preserve completed results; provider errors never become successful evaluations.
                    suite = suite with { Status = "failed", CompletedAt = DateTimeOffset.UtcNow,
                        FailureCode = lifetime.IsCancellationRequested ? "interrupted" : "execution_failed" };
                }
                finally
                {
                    try { store.SaveSuite(suite); }
                    finally
                    {
                        lock (runGate) { active.Remove(suite.Id); capacity.Release(); }
                    }
                }
            }, CancellationToken.None);
            active.Add(suite.Id, task);
            return suite;
        }
    }
    public Task StartAsync(CancellationToken token)
    {
        // The operation journal owns a single backend process. A running record
        // from an earlier process is an interrupted paid job, never a replay request.
        store.InterruptRunningSuites();
        return Task.CompletedTask;
    }
    public async Task StopAsync(CancellationToken token)
    {
        Task[] pending;
        lock (runGate)
        {
            stopping = true;
            shutdown.Cancel();
            pending = active.Values.ToArray();
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { await Task.WhenAll(pending).WaitAsync(deadline.Token); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        finally { store.InterruptRunningSuites(); }
    }
    public void Dispose()
    {
        lock (runGate) { stopping = true; shutdown.Cancel(); }
        // StopAsync drains active work before DI tears down the provider runtime.
        // Retain managed cancellation/semaphore state if a timed-out call unwinds later.
    }
    private static void ValidateRequest(LabSuiteRequest request)
    {
        LabContract.ExecutionPlatform(request.ExecutionPlatform);
        if (!LabContract.Platform(request.Platform) || request.ScenarioIds is null || request.ScenarioIds.Length < 1
            || request.ScenarioIds.Length > Math.Min(32, LabContract.Scenarios.Length)
            || request.ScenarioIds.Distinct(StringComparer.Ordinal).Count() != request.ScenarioIds.Length)
        { throw new LabException(400, "invalid_lab_suite"); }
        var total = 0;
        foreach (var id in request.ScenarioIds)
        {
            var scenario = LabContract.Scenarios.SingleOrDefault(x => x.Id == id) ?? throw new LabException(400, "invalid_lab_suite");
            var script = Script(scenario);
            if (script.Length is < 1 or > 8 || script.Where((turn, index) => turn.Index != index + 1
                    || string.IsNullOrWhiteSpace(turn.Input) || turn.Input.Length > 4000 || turn.Checks.Length > 16).Any())
            { throw new LabException(400, "invalid_lab_script"); }
            total += script.Length;
        }
        if (total > 120) { throw new LabException(400, "lab_suite_turn_limit"); }
    }
    private static LabScriptTurn[] Script(LabScenario scenario) => scenario.Script.Length > 0 ? scenario.Script
        : [new(1, scenario.OpeningText, ["nonempty-output", "short-reply", "no-explicit-movement-claim"])];

    private async Task RunScenarioAsync(LabSession session, Func<CancellationToken, Task<bool>> authorized,
        CancellationToken token, Action<LabSuiteResult> progress, string? executionPlatform)
    {
        using var reservation = runtime.ReserveScript(session.OwnerId, session.Id, out var scriptOwner);
        var scenario = session.ScenarioSnapshot ?? throw new LabException(400, "lab_script_snapshot_missing");
        var script = Script(scenario);
        var result = new LabSuiteResult(scenario.Id, session.Id, "running", [])
        {
            ScenarioSha256 = scenario.ScriptSha256.Length > 0 ? scenario.ScriptSha256
                : LabContract.Hash(System.Text.Json.JsonSerializer.Serialize(script))
        };
        progress(result);
        try
        {
            foreach (var step in script)
            {
                token.ThrowIfCancellationRequested();
                if (!await authorized(token)) { throw new LabException(401, "account_session_revoked"); }
                var prior = runtime.Detail(session.OwnerId, session.Id);
                var before = prior.Turns.Select(turn => turn.Id).ToHashSet(StringComparer.Ordinal);
                var beforeEvents = prior.Events.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
                LabTurnObservation observation;
                try
                {
                    var observed = await AuthorizedTurnAsync(session, step.Input, authorized, token, scriptOwner, executionPlatform);
                    var added = observed.Turns.Where(turn => !before.Contains(turn.Id)).ToArray();
                    var visitor = added.LastOrDefault(turn => turn.Role == "user");
                    var assistant = added.LastOrDefault(turn => turn.Role == "assistant");
                    observation = new(step.Index, visitor?.Id, assistant?.Id, "completed", assistant?.LatencyMs,
                        assistant?.AudioBytes, LabRubric.InspectTurn(step, assistant, observed.Events.Where(entry => !beforeEvents.Contains(entry.Id)).ToArray()));
                }
                catch (LabException error)
                {
                    var visitor = runtime.Detail(session.OwnerId, session.Id).Turns.LastOrDefault(turn => !before.Contains(turn.Id) && turn.Role == "user");
                    observation = new(step.Index, visitor?.Id, null, "failed", null, null,
                        [new("provider-turn", false, "Tah nebyl dokončen. Technický kód aplikace: " + error.Code),
                            .. step.Checks.Select(LabRubric.NotExercised)]);
                }
                result = result with
                {
                    TurnObservations = [.. result.TurnObservations, observation],
                    Assertions = [.. result.Assertions, .. observation.Assertions.Select(assertion => assertion with { Name = $"turn-{step.Index}:" + assertion.Name })]
                };
                progress(result);
                if (observation.Status == "failed") { break; }
            }
            result = result with { Status = result.TurnObservations.Length == script.Length
                && result.TurnObservations.All(observation => observation.Status == "completed") ? "completed" : "failed" };
        }
        finally
        {
            if (result.Status == "running") { result = result with { Status = "failed" }; }
            try { progress(result); }
            finally { runtime.End(session.OwnerId, session.Id); }
        }
    }
    private async Task<LabSession> AuthorizedTurnAsync(LabSession session, string input,
        Func<CancellationToken, Task<bool>> authorized, CancellationToken suiteToken, Guid scriptOwner, string? executionPlatform)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(suiteToken);
        var work = runtime.TurnAsync(session.OwnerId, session.Id, input, lifetime.Token, scriptOwner, executionPlatform);
        var monitor = MonitorAsync();
        try
        {
            if (await Task.WhenAny(work, monitor) == monitor) { await monitor; }
            return await work;
        }
        finally
        {
            lifetime.Cancel();
            try { await monitor; }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            if (!work.IsCompleted)
            {
                try { await work; }
                catch (Exception error) when (error is not OutOfMemoryException) { }
            }
        }
        async Task MonitorAsync()
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), lifetime.Token);
                if (!await authorized(lifetime.Token)) { throw new LabException(401, "account_session_revoked"); }
            }
        }
    }
}
