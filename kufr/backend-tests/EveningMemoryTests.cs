using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

internal static class EveningMemoryTests
{
    internal static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"kufr-evening-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Backend:DatabasePath"] = Path.Combine(directory, "journal.sqlite"),
                    ["Backend:Usage:SessionCapMicroUsd"] = "500000",
                    ["Backend:Usage:DailyCapMicroUsd"] = "10000000",
                    ["Backend:Usage:MonthlyCapMicroUsd"] = "10000000"
                }).Build();
            using (var journal = new OperationJournal(configuration, TimeProvider.System))
            {
                var memory = new EveningMemoryStore(configuration, journal);
                var first = memory.GetStatus("device-a");
                Check(memory.Append("device-a", first.EveningId, "op-1", "petr-first",
                    "Jsem Petr. Objednal jsem taxik k sousedum.", "Takze sousedi uz jsou doma?"), "append");
                Check(!memory.Append("device-a", first.EveningId, "op-1", "petr-first",
                    "duplicate", "duplicate"), "idempotent append");
                for (var index = 0; index < 35; index++)
                {
                    memory.Append("device-a", first.EveningId, $"other-{index}", $"other-{index}",
                        new string('x', 900), new string('y', 450));
                }

                var recalled = memory.ReadContext("device-a", "return", "Petr je zpatky.");
                Check(recalled.Transcript.Contains("taxik", StringComparison.Ordinal),
                    "matching old name survives beyond recent context window");
                Check(recalled.Transcript.Length <= EveningMemoryStore.MaxContextCharacters,
                    "context is bounded");
                using var parsed = JsonDocument.Parse(recalled.Transcript);
                Check(parsed.RootElement.GetArrayLength() > 0, "context remains whole JSON records");
                Check(memory.ReadContext("device-b", "return", "Petr").Transcript == "",
                    "different device cannot recall content");
            }

            using (var journal = new OperationJournal(configuration, TimeProvider.System))
            {
                var memory = new EveningMemoryStore(configuration, journal);
                Check(memory.ReadContext("device-a", "return", "Petr").Transcript.Contains("taxik"),
                    "evening survives backend restart");
                var first = memory.GetStatus("device-a");
                Check(memory.Clear("device-a", first.EveningId), "clear expected evening");
                var second = memory.GetStatus("device-a");
                Check(first.EveningId != second.EveningId && second.ExchangeCount == 0,
                    "clear rotates identity and removes content");
                Check(!memory.Append("device-a", first.EveningId, "late", "old", "Petr", "late"),
                    "late completion cannot recreate cleared memory");
                memory.Append("device-a", second.EveningId, "new", "new", "Jana", "Ahoj");
                Check(!memory.Clear("device-a", first.EveningId)
                    && memory.GetStatus("device-a").ExchangeCount == 1,
                    "retry cannot clear the next evening");
                Check(!memory.Clear("device-b", second.EveningId), "clear is device scoped");
                for (var index = 0; index < EveningMemoryStore.MaxExchanges; index++)
                {
                    memory.Append("device-a", second.EveningId, $"limit-{index}", "limit", "a", "b");
                }

                var bounded = memory.GetStatus("device-a");
                Check(bounded.ExchangeCount == EveningMemoryStore.MaxExchanges
                    && bounded.DroppedExchangeCount == 1, "row cap drops only oldest exchange");
                memory.Clear("device-a", bounded.EveningId);
                var byteEvening = memory.GetStatus("device-a");
                for (var index = 0; index < 450; index++)
                {
                    memory.Append("device-a", byteEvening.EveningId, $"bytes-{index}", "bytes",
                        new string('\u4e00', 1000), new string('\u4e00', 500));
                }

                var byteBounded = memory.GetStatus("device-a");
                Check(byteBounded.ContentBytes <= EveningMemoryStore.MaxContentBytes
                    && byteBounded.DroppedExchangeCount > 0, "UTF-8 byte cap is enforced");
                memory.Clear("device-a", byteBounded.EveningId);
                await CheckRuntimeAsync(journal, memory);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task CheckRuntimeAsync(OperationJournal journal, EveningMemoryStore memory)
    {
        var calls = new List<(string Text, string Context)>();
        TaskCompletionSource<TextDialogResponse>? delayed = null;
        using var runner = new TextDialogRunner((text, _, _, _, _, context) =>
        {
            calls.Add((text, context));
            return delayed?.Task ?? Task.FromResult(Reply("Jak ti rikaji?"));
        });
        var store = new ConversationStore(TimeProvider.System, journal, runner, eveningMemory: memory);
        var boot = Guid.NewGuid();
        string Create() => store.Create("device-a", Guid.NewGuid(), "create"u8,
            new CreateConversationRequest("device-a", boot, 0, "catalog", "cs-CZ",
                new ConversationConsent(false, false))).Response!.ConversationId;
        TextTurnRequest Turn(string text) => new(Guid.NewGuid(), boot, 0, text, 63, 1, "catalog");
        var conversation = Create();
        var opening = Turn("");
        var key = Guid.NewGuid();
        var first = store.SubmitTurn("device-a", conversation, key, "opening"u8, opening, opening: true);
        await CompletedAsync(store, runner, first.OperationId!);
        Check(calls[0].Text == "" && calls[0].Context == "", "suitcase begins with no invented visitor message");
        Check(store.SubmitTurn("device-a", conversation, key, "opening"u8, opening, opening: true).OperationId
            == first.OperationId && calls.Count == 1, "opening retry does not call model twice");
        Check(store.SubmitTurn("device-a", conversation, Guid.NewGuid(), "other-opening"u8,
            Turn(""), opening: true).Status == 409, "opening cannot repeat inside a conversation");
        var answer = store.SubmitTurn("device-a", conversation, Guid.NewGuid(), "answer"u8,
            Turn("Jsem Petr a taxik jsem objednal k sousedum."));
        await CompletedAsync(store, runner, answer.OperationId!);
        store.EndConversation("device-a", conversation, Guid.NewGuid());
        conversation = Create();
        var returning = store.SubmitTurn("device-a", conversation, Guid.NewGuid(), "returning"u8,
            Turn("Petr, uz jsem tu byl."));
        await CompletedAsync(store, runner, returning.OperationId!);
        Check(calls[^1].Context.Contains("sousedum"), "next visitor session recalls earlier answers");
        var oldEvening = memory.GetStatus("device-a").EveningId;
        delayed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = store.SubmitTurn("device-a", conversation, Guid.NewGuid(), "pending"u8, Turn("late secret"));
        for (var attempt = 0; calls.Count < 4 && attempt < 100; attempt++)
        {
            await Task.Delay(10);
        }

        Check(calls.Count == 4, "delayed provider started");
        store.ClearEveningMemory("device-a", oldEvening);
        delayed.SetResult(Reply("late secret answer"));
        // Wait until the runner releases its slot, proving completion has unwound.
        var released = false;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (runner.TryAcquire())
            {
                runner.Release();
                released = true;
                break;
            }
            await Task.Delay(10);
        }

        Check(released && memory.GetStatus("device-a").ExchangeCount == 0,
            "clear cancels old conversation and suppresses late memory write");
        Check(store.GetOperation("device-a", pending.OperationId!).Operation?.Status != "completed",
            "late response is not published after clear");
    }

    private static TextDialogResponse Reply(string text) =>
        new(text, "ask", "", null, ResponsesTextDialogBudget.Model, 100, 30, "default");

    private static async Task CompletedAsync(ConversationStore store, TextDialogRunner runner, string operationId)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var result = store.GetOperation("device-a", operationId);
            if (result.Operation?.Status == "completed" && runner.TryAcquire())
            {
                runner.Release();
                return;
            }
            await Task.Delay(10);
        }
        throw new Exception("Evening operation did not complete.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }
}
