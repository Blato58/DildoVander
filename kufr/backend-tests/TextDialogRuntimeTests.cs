using System.Text;
using Kufr.Backend.Features.Conversations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

internal static class TextDialogRuntimeTests
{
    internal static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"kufr-dialog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            CheckModeConfiguration(directory);
            await RunSuccessfulTurnAsync(directory);
            await RunMultiTurnDialogueAsync(directory);
            await RunPromptCancellationAsync(directory);
            await RunCancelledTurnAsync(directory);
            await RunFailedTurnAsync(directory);
            await RunOversizedRequestAsync(directory);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CheckModeConfiguration(string directory)
    {
        var lifetime = new TestLifetime();
        var fakeConfiguration = new ConfigurationBuilder().Build();
        using (var fake = new TextDialogRunner(fakeConfiguration, lifetime))
        {
            Require(!fake.Enabled, "keyless default mode remains fake");
        }

        var missingKey = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:Dialog:Mode"] = "responses"
            })
            .Build();
        try
        {
            using var _ = new TextDialogRunner(missingKey, lifetime);
            throw new Exception("Responses mode started without a key or caps.");
        }
        catch (InvalidOperationException)
        {
        }

        var enabled = new ConfigurationBuilder()
            .AddConfiguration(BuildConfiguration(Path.Combine(directory, "config.sqlite"), 500_000))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:Dialog:Mode"] = "responses",
                ["OPENAI_API_KEY"] = "sk-test-only"
            })
            .Build();
        using var configured = new TextDialogRunner(enabled, lifetime);
        Require(configured.Enabled, "explicit Responses mode accepts key and all caps");
    }

    private static async Task RunSuccessfulTurnAsync(string directory)
    {
        var configuration = BuildConfiguration(Path.Combine(directory, "success.sqlite"), 100_000);
        using var journal = new OperationJournal(configuration, TimeProvider.System);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<TextDialogResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var runner = new TextDialogRunner((_, _, _, _, _) =>
        {
            started.SetResult(true);
            return finish.Task;
        });
        var store = new ConversationStore(TimeProvider.System, journal, runner);
        var boot = Guid.NewGuid();
        var created = store.Create(
            "device-a", Guid.NewGuid(), "create"u8,
            new CreateConversationRequest(
                "device-a", boot, 0, "neutral-1", "cs-CZ", new ConversationConsent(false, false)));
        Require(created.Response is not null, "paid conversation created");
        var conversationId = created.Response!.ConversationId;
        var request = new TextTurnRequest(
            Guid.NewGuid(), boot, 0, "Syntetický party test", 1, 3, "neutral-1");
        var key = Guid.NewGuid();
        var body = Encoding.UTF8.GetBytes("synthetic request");
        var accepted = store.SubmitTurn("device-a", conversationId, key, body, request);
        Require(accepted.Status == 202 && accepted.OperationId is not null,
            "paid turn reserved before dispatch");
        var operationId = accepted.OperationId!;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(store.SubmitTurn("device-a", conversationId, key, body, request).OperationId
            == accepted.OperationId, "idempotent retry does not call provider again");
        Require(store.SubmitTurn(
            "device-a", conversationId, Guid.NewGuid(), body, request).ErrorCode
            == "duplicate_turn_id", "same turn with a new key cannot spend twice");
        Require(store.SubmitTurn(
            "device-a", conversationId, Guid.NewGuid(), body,
            request with { ClientTurnId = Guid.NewGuid() }).ErrorCode == "dialogue_busy",
            "second paid call is rejected before reservation");

        finish.SetResult(new TextDialogResponse(
            "Jaký průšvih má hůlka napravit?", "ask", "", null,
            ResponsesTextDialogBudget.Model, 621, 102, "default"));
        var completed = await PollAsync(store, runner, operationId, "completed");
        var result = completed.Result as TurnResult;
        Require(result?.Decision == "ask" && result.Proposal is null
            && result.Reply == "Jaký průšvih má hůlka napravit?",
            "first-turn clarification comes from the provider");
        Require(journal.SettleCost(operationId, 2573)
            && !journal.SettleCost(operationId, 0),
            "provider token usage was settled exactly once");
    }

    private static async Task RunFailedTurnAsync(string directory)
    {
        var configuration = BuildConfiguration(Path.Combine(directory, "failure.sqlite"), ResponsesTextDialogBudget.ReservedMicroUsd);
        using var journal = new OperationJournal(configuration, TimeProvider.System);
        using var runner = new TextDialogRunner((_, _, _, _, _) =>
            throw new TextDialogUnavailableException("Synthetic provider failure."));
        var store = new ConversationStore(TimeProvider.System, journal, runner);
        var boot = Guid.NewGuid();
        var created = store.Create(
            "device-a", Guid.NewGuid(), "create"u8,
            new CreateConversationRequest(
                "device-a", boot, 0, "neutral-1", "cs-CZ", new ConversationConsent(false, false)));
        var conversationId = created.Response!.ConversationId;
        var request = new TextTurnRequest(
            Guid.NewGuid(), boot, 0, "Syntetický výpadek", 1, 3, "neutral-1");
        var first = store.SubmitTurn(
            "device-a", conversationId, Guid.NewGuid(), "first"u8, request);
        Require(first.OperationId is not null, "failed provider request was reserved");
        var failed = await PollAsync(store, runner, first.OperationId!, "failed");
        Require(failed.Error is not null, "provider failure is visible without visitor text");
        var second = store.SubmitTurn(
            "device-a", conversationId, Guid.NewGuid(), "second"u8,
            request with { ClientTurnId = Guid.NewGuid() });
        Require(second.Status == 429 && second.ErrorCode == "cost_limit",
            "ambiguous provider failure keeps the reservation");
    }

    private static async Task RunMultiTurnDialogueAsync(string directory)
    {
        var configuration = BuildConfiguration(Path.Combine(directory, "multi-turn.sqlite"), 500_000);
        using var journal = new OperationJournal(configuration, TimeProvider.System);
        var seenHistory = new List<DialogExchange[]>();
        var requiredQuestions = new List<bool>();
        using var runner = new TextDialogRunner((_, _, history, requireQuestion, _) =>
        {
            seenHistory.Add(history.ToArray());
            requiredQuestions.Add(requireQuestion);
            var response = seenHistory.Count switch
            {
                1 => new TextDialogResponse("Jaký trapas má magie napravit?", "ask", "", null,
                    ResponsesTextDialogBudget.Model, 100, 20, "default"),
                2 => new TextDialogResponse("Jaký druh magie?", "ask", "", null,
                    ResponsesTextDialogBudget.Model, 200, 20, "default"),
                _ => new TextDialogResponse("Třpyt a chaos? Hůlka dvě tě zvládne líp než ty svůj plán.",
                    "propose", "Třpyt a chaos", 2,
                    ResponsesTextDialogBudget.Model, 300, 30, "default")
            };
            return Task.FromResult(response);
        });
        var store = new ConversationStore(TimeProvider.System, journal, runner);
        var boot = Guid.NewGuid();
        var created = store.Create("device-a", Guid.NewGuid(), "create"u8,
            new CreateConversationRequest("device-a", boot, 0, "neutral-1", "cs-CZ",
                new ConversationConsent(false, false)));
        var conversationId = created.Response!.ConversationId;
        var answers = new[] { "Chci hůlku.", "Třpyt.", "Chaos." };
        for (var index = 0; index < answers.Length; index++)
        {
            var request = new TextTurnRequest(Guid.NewGuid(), boot, 0, answers[index], 2, 7,
                "neutral-1");
            var accepted = store.SubmitTurn("device-a", conversationId, Guid.NewGuid(),
                Encoding.UTF8.GetBytes($"multi-{index}"), request);
            Require(accepted.OperationId is not null, "multi-turn request was accepted");
            var operation = await PollAsync(store, runner, accepted.OperationId!, "completed");
            var result = (TurnResult)operation.Result!;
            if (index < 2)
            {
                Require(result.Decision == "ask" && result.Proposal is null,
                    "two completed questions precede any proposal");
            }
            else
            {
                var proposal = result.Proposal as SelectionProposal;
                Require(result.Decision == "propose" && proposal?.Compartment == 2
                    && proposal.ConversationId == conversationId
                    && proposal.TurnId == request.ClientTurnId
                    && proposal.DeviceRevision == request.DeviceRevision,
                    "third turn may publish a correlated proposal");
            }
        }

        Require(seenHistory.Count == 3
            && seenHistory[0].Length == 0
            && seenHistory[1].Length == 1
            && seenHistory[1][0].Reply.Contains("trapas", StringComparison.Ordinal)
            && seenHistory[2].Length == 2
            && seenHistory[2][1].VisitorText == "Třpyt.",
            "provider receives completed question and answer pairs in order");
        Require(requiredQuestions.SequenceEqual(new[] { true, true, false }),
            "only first two turns require a model-generated question");
        Require(store.EndConversation("device-a", conversationId, Guid.NewGuid()).Status == 204,
            "ending conversation discards its RAM history");
        var next = store.Create("device-a", Guid.NewGuid(), "next"u8,
            new CreateConversationRequest("device-a", boot, 1, "neutral-1", "cs-CZ",
                new ConversationConsent(false, false)));
        Require(next.Status == 201 && next.Response is not null,
            "new conversation starts after old history is cleared");
    }

    private static async Task RunCancelledTurnAsync(string directory)
    {
        var configuration = BuildConfiguration(Path.Combine(directory, "cancelled.sqlite"), ResponsesTextDialogBudget.ReservedMicroUsd);
        using var journal = new OperationJournal(configuration, TimeProvider.System);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<TextDialogResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var runner = new TextDialogRunner((_, _, _, _, _) =>
        {
            started.SetResult(true);
            return finish.Task;
        });
        var store = new ConversationStore(TimeProvider.System, journal, runner);
        var boot = Guid.NewGuid();
        var created = store.Create(
            "device-a", Guid.NewGuid(), "create"u8,
            new CreateConversationRequest(
                "device-a", boot, 0, "neutral-1", "cs-CZ", new ConversationConsent(false, false)));
        var request = new TextTurnRequest(
            Guid.NewGuid(), boot, 0, "Zrušený test", 1, 3, "neutral-1");
        var accepted = store.SubmitTurn(
            "device-a", created.Response!.ConversationId, Guid.NewGuid(), "cancel"u8, request);
        var operationId = accepted.OperationId ?? throw new Exception("Cancelled turn was not accepted.");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(store.CancelOperation("device-a", operationId, Guid.NewGuid()).Status == 204,
            "in-flight request can be cancelled");
        finish.SetResult(new TextDialogResponse(
            "Pozdní odpověď", "propose", "", 1,
            ResponsesTextDialogBudget.Model, 621, 102, "default"));
        var acquired = false;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            acquired = runner.TryAcquire();
            if (acquired)
            {
                break;
            }

            await Task.Delay(25);
        }

        Require(acquired,
            "test acquired the released slot after the provider finished");
        runner.Release();
        Require(store.GetOperation("device-a", operationId).Operation?.Status == "cancelled",
            "late provider result was not published after cancellation");
        Require(journal.SettleCost(operationId, 2573),
            "late provider usage was charged despite cancellation");
    }

    private static async Task RunOversizedRequestAsync(string directory)
    {
        var configuration = BuildConfiguration(Path.Combine(directory, "oversized.sqlite"), ResponsesTextDialogBudget.ReservedMicroUsd);
        using var journal = new OperationJournal(configuration, TimeProvider.System);
        using var runner = new TextDialogRunner((_, _, _, _, _) =>
            throw new TextDialogRequestTooLargeException());
        var store = new ConversationStore(TimeProvider.System, journal, runner);
        var boot = Guid.NewGuid();
        var created = store.Create("device-a", Guid.NewGuid(), "create"u8,
            new CreateConversationRequest("device-a", boot, 0, "neutral-1", "cs-CZ",
                new ConversationConsent(false, false)));
        var request = new TextTurnRequest(Guid.NewGuid(), boot, 0, "Syntetický test", 1, 3,
            "neutral-1");
        var first = store.SubmitTurn("device-a", created.Response!.ConversationId,
            Guid.NewGuid(), "first"u8, request);
        Require(first.OperationId is not null, "oversized request reserved");
        await PollAsync(store, runner, first.OperationId!, "failed");
        var second = store.SubmitTurn("device-a", created.Response.ConversationId,
            Guid.NewGuid(), "second"u8, request with { ClientTurnId = Guid.NewGuid() });
        Require(second.Status == 202,
            "pre-send request rejection releases the full cost reservation");
        await PollAsync(store, runner, second.OperationId!, "failed");
    }

    private static async Task RunPromptCancellationAsync(string directory)
    {
        var configuration = BuildConfiguration(Path.Combine(directory, "prompt-cancel.sqlite"), ResponsesTextDialogBudget.ReservedMicroUsd);
        using var journal = new OperationJournal(configuration, TimeProvider.System);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var runner = new TextDialogRunner(async (_, _, _, _, token) =>
        {
            started.SetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new Exception("Cancelled provider unexpectedly completed.");
        });
        var store = new ConversationStore(TimeProvider.System, journal, runner);
        var boot = Guid.NewGuid();
        var created = store.Create(
            "device-a", Guid.NewGuid(), "create"u8,
            new CreateConversationRequest(
                "device-a", boot, 0, "neutral-1", "cs-CZ", new ConversationConsent(false, false)));
        var request = new TextTurnRequest(
            Guid.NewGuid(), boot, 0, "Přerušený test", 1, 3, "neutral-1");
        var conversationId = created.Response!.ConversationId;
        var accepted = store.SubmitTurn(
            "device-a", conversationId, Guid.NewGuid(), "cancel"u8, request);
        var operationId = accepted.OperationId ?? throw new Exception("Turn was not accepted.");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(store.CancelOperation("device-a", operationId, Guid.NewGuid()).Status == 204,
            "running provider cancellation accepted");

        var released = false;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            released = runner.TryAcquire();
            if (released)
            {
                break;
            }

            await Task.Delay(25);
        }

        Require(released, "cancelled provider released the only paid slot promptly");
        runner.Release();
        Require(store.GetOperation("device-a", operationId).Operation?.Status == "cancelled",
            "cancelled operation remains cancelled");
        var next = store.SubmitTurn(
            "device-a", conversationId, Guid.NewGuid(), "next"u8,
            request with { ClientTurnId = Guid.NewGuid() });
        Require(next.ErrorCode == "cost_limit",
            "sent but cancelled call conservatively retains its reservation");
    }

    private static IConfiguration BuildConfiguration(string path, long sessionCap)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = path,
            ["Backend:Usage:SessionCapMicroUsd"] = sessionCap.ToString(),
            ["Backend:Usage:DailyCapMicroUsd"] = "10000000",
            ["Backend:Usage:MonthlyCapMicroUsd"] = "10000000"
        }).Build();
    }

    private static async Task<TurnOperation> PollAsync(
        ConversationStore store,
        TextDialogRunner runner,
        string operationId,
        string expectedStatus)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var operation = store.GetOperation("device-a", operationId).Operation;
            if (operation?.Status == expectedStatus && runner.TryAcquire())
            {
                // Terminal publication precedes the worker's finally block.
                // Wait for slot cleanup before this test submits its next turn.
                runner.Release();
                return operation;
            }

            await Task.Delay(25);
        }

        throw new Exception($"Operation did not reach {expectedStatus}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    private sealed class TestLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
