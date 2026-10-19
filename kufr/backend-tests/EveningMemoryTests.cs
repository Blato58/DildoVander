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
            var legacyEveningId = Guid.NewGuid().ToString("D");
            using (var connection = new SqliteConnection(
                $"Data Source={Path.Combine(directory, "journal.sqlite.evening.sqlite")}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE evenings (
                        device_id TEXT PRIMARY KEY, evening_id TEXT NOT NULL,
                        dropped_count INTEGER NOT NULL DEFAULT 0);
                    CREATE TABLE exchanges (
                        sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                        device_id TEXT NOT NULL, evening_id TEXT NOT NULL,
                        operation_id TEXT NOT NULL UNIQUE, conversation_id TEXT NOT NULL,
                        visitor_text TEXT NOT NULL, reply TEXT NOT NULL, content_bytes INTEGER NOT NULL);
                    INSERT INTO evenings VALUES ('legacy-device', $evening, 3);
                    INSERT INTO exchanges(device_id, evening_id, operation_id, conversation_id,
                        visitor_text, reply, content_bytes)
                    VALUES ('legacy-device', $evening, 'legacy-op', 'legacy-conversation',
                        'Legacy visitor story', 'Legacy reply', 33);
                    """;
                command.Parameters.AddWithValue("$evening", legacyEveningId);
                command.ExecuteNonQuery();
            }

            using (var journal = new OperationJournal(configuration, TimeProvider.System))
            {
                var memory = new EveningMemoryStore(configuration, journal);
                var legacy = memory.List("legacy-device");
                Check(legacy.ActiveEveningId == legacyEveningId
                    && legacy.Evenings.Single().DroppedCount == 3
                    && legacy.Evenings.Single().ExchangeCount == 1,
                    "legacy evening ID, content and dropped count migrate");
                Check(memory.ReadContext("legacy-device", "legacy-conversation", "Legacy").Transcript.Contains("Legacy visitor"),
                    "legacy story remains readable");
                Check(!memory.Append("legacy-device", legacyEveningId, "legacy-op", "duplicate", "x", "y"),
                    "legacy operation ID remains unique");
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

                var recalled = memory.ReadContext("device-a", "petr-first", "Petr je zpatky.");
                Check(recalled.Transcript.Contains("taxik", StringComparison.Ordinal),
                    "same conversation retains its older statement beyond recent context window");
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
                Check(memory.ReadContext("device-a", "petr-first", "Petr").Transcript.Contains("taxik"),
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
                Check(bounded.ExchangeCount == EveningMemoryStore.MaxExchanges + 1
                    && bounded.DroppedExchangeCount == 0, "archive survives the former row trimming threshold");
                memory.Clear("device-a", bounded.EveningId);
                var byteEvening = memory.GetStatus("device-a");
                for (var index = 0; index < 450; index++)
                {
                    memory.Append("device-a", byteEvening.EveningId, $"bytes-{index}", "bytes",
                        new string('\u4e00', 1000), new string('\u4e00', 500));
                }

                var byteBounded = memory.GetStatus("device-a");
                Check(byteBounded.ContentBytes > EveningMemoryStore.MaxContentBytes
                    && byteBounded.DroppedExchangeCount == 0, "archive survives the former UTF-8 byte trimming threshold");
                memory.Clear("device-a", byteBounded.EveningId);
                CheckProfiles(journal, memory);
                CheckMutationRetention(directory, memory);
                CheckEveningLimit(directory, memory);
                await CheckRuntimeAsync(journal, memory);
            }

            CheckRestartReplay(directory);
            await CheckPaidBusyAfterExpiryAsync(directory);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CheckProfiles(OperationJournal journal, EveningMemoryStore memory)
    {
        Check(EveningMemoryStore.IsValidName("🎉")
            && EveningMemoryStore.IsValidName(string.Concat(Enumerable.Repeat("🎉", 80)))
            && EveningMemoryStore.IsValidName(new string('x', 80))
            && !EveningMemoryStore.IsValidName(new string('x', 81))
            && !EveningMemoryStore.IsValidName(" ")
            && !EveningMemoryStore.IsValidName("bad\nname")
            && !EveningMemoryStore.IsValidName("bad\u200bname"),
            "names are bounded by Unicode scalar count and reject controls");

        using var runner = new TextDialogRunner((_, _, _, _, _, _) =>
            Task.FromResult(Reply("Test")));
        var store = new ConversationStore(TimeProvider.System, journal, runner, eveningMemory: memory);
        var initial = store.ListEvenings("profile-a")!;
        var initialId = initial.ActiveEveningId;
        Check(initial.Evenings.Count == 1, "new device begins with one empty evening");
        Check(memory.Append("profile-a", initialId, "profile-op-1", "petr", "Petr story", "Ahoj"),
            "initial profile receives memory");
        var createKey = Guid.NewGuid();
        var createBody = Encoding.UTF8.GetBytes($"{{\"name\":\"Nový večer\",\"expectedEveningId\":\"{initialId}\"}}");
        var created = store.CreateEvening("profile-a", createKey, createBody, initialId, "Nový večer");
        var nextId = created.Response!.ActiveEveningId;
        Check(created.Status == 201 && nextId != initialId && created.Response.Evenings.Count == 2,
            "create activates a second profile and archives the first");
        Check(!memory.Append("profile-a", initialId, "late-profile-op", "petr", "late", "late")
            && memory.ReadContext("profile-a", "petr", "Petr").Transcript == "",
            "late append to archived profile is rejected and active context is isolated");
        Check(store.CreateEvening("profile-a", Guid.NewGuid(), createBody, initialId, "Nový večer")
            .ErrorCode == "evening_conflict", "stale expected ID rejects create");
        Check(store.CreateEvening("profile-a", createKey, "different"u8, initialId, "Nový večer")
            .ErrorCode == "idempotency_conflict", "same key with changed body conflicts");
        var replay = store.CreateEvening("profile-a", createKey, createBody, initialId, "Nový večer");
        Check(replay.Status == 201 && replay.Response!.ActiveEveningId == nextId,
            "create replay returns its original snapshot despite stale CAS");
        var foreign = store.ActivateEvening("profile-b", Guid.NewGuid(), "foreign"u8,
            initialId, store.ListEvenings("profile-b")!.ActiveEveningId);
        Check(foreign.Status == 404, "another device cannot switch into a foreign evening");

        var boot = Guid.NewGuid();
        var busyConversation = store.Create("profile-a", Guid.NewGuid(), "busy"u8,
            new CreateConversationRequest("profile-a", boot, 0, "catalog", "cs-CZ",
                new ConversationConsent(false, false))).Response!.ConversationId;
        Check(store.ActivateEvening("profile-a", Guid.NewGuid(), "busy-switch"u8,
            initialId, nextId).ErrorCode == "evening_busy", "live conversation blocks its device switch");
        Check(store.CreateEvening("profile-b", Guid.NewGuid(), "other-device"u8,
            store.ListEvenings("profile-b")!.ActiveEveningId, "Other").Status == 201,
            "one device's conversation does not block another device");
        store.EndConversation("profile-a", busyConversation, Guid.NewGuid());

        var activateKey = Guid.NewGuid();
        var activateBody = Encoding.UTF8.GetBytes($"{{\"expectedEveningId\":\"{nextId}\"}}");
        var activated = store.ActivateEvening("profile-a", activateKey, activateBody, initialId, nextId);
        Check(activated.Status == 200 && activated.Response!.ActiveEveningId == initialId
            && memory.ReadContext("profile-a", "petr", "Petr").Transcript.Contains("Petr story"),
            "switch restores archived content");
        Check(store.ActivateEvening("profile-a", Guid.NewGuid(), activateBody, nextId, nextId)
            .ErrorCode == "evening_conflict", "stale expected ID rejects switch");
        Check(store.ActivateEvening("profile-a", activateKey, activateBody, initialId, nextId)
            .Response!.ActiveEveningId == initialId, "switch retry replays same response");
        Check(memory.Clear("profile-a", initialId), "legacy clear removes current evening");
        Check(memory.List("profile-a").Evenings.Any(item => item.EveningId == nextId)
            && !memory.List("profile-a").Evenings.Any(item => item.EveningId == initialId),
            "legacy clear preserves archived evenings");

        // Persisted response is a snapshot. Replaying it must never move the active pointer.
        var secondReplay = store.CreateEvening("profile-a", createKey, createBody, initialId, "Nový večer");
        Check(secondReplay.Response!.ActiveEveningId == nextId
            && memory.List("profile-a").ActiveEveningId != nextId,
            "old replay returns original snapshot without reactivation");
    }

    private static async Task CheckPaidBusyAfterExpiryAsync(string directory)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Backend:DatabasePath"] = Path.Combine(directory, "expiry.sqlite"),
                ["Backend:Usage:SessionCapMicroUsd"] = "500000",
                ["Backend:Usage:DailyCapMicroUsd"] = "10000000",
                ["Backend:Usage:MonthlyCapMicroUsd"] = "10000000"
            }).Build();
        var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        using var journal = new OperationJournal(configuration, clock);
        var memory = new EveningMemoryStore(configuration, journal);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<TextDialogResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var runner = new TextDialogRunner((_, _, _, _, _, _) =>
        {
            started.TrySetResult();
            return release.Task;
        });
        var store = new ConversationStore(clock, journal, runner, eveningMemory: memory);
        var initialId = memory.GetStatus("expiring-device").EveningId;
        var boot = Guid.NewGuid();
        var conversation = store.Create("expiring-device", Guid.NewGuid(), "create"u8,
            new CreateConversationRequest("expiring-device", boot, 0, "catalog", "cs-CZ",
                new ConversationConsent(false, false))).Response!.ConversationId;
        store.SubmitTurn("expiring-device", conversation, Guid.NewGuid(), "turn"u8,
            new TextTurnRequest(Guid.NewGuid(), boot, 0, "visitor", 63, 1, "catalog"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromMinutes(16));
        var blocked = store.CreateEvening("expiring-device", Guid.NewGuid(), "new-evening"u8,
            initialId, "After expiry");
        Check(blocked.ErrorCode == "evening_busy",
            "expired journal/conversation still blocks switch while provider ignores cancellation");
        var otherInitialId = memory.GetStatus("independent-device").EveningId;
        Check(store.CreateEvening("independent-device", Guid.NewGuid(), "other-evening"u8,
            otherInitialId, "Independent").Status == 201,
            "expired provider call does not block other device");
        release.SetResult(Reply("late"));
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (runner.TryAcquire())
            {
                runner.Release();
                return;
            }

            await Task.Delay(10);
        }

        throw new Exception("Expired provider call did not unwind.");
    }

    private static void CheckMutationRetention(string directory, EveningMemoryStore memory)
    {
        const string deviceId = "mutation-cap-device";
        const string route = "/v1/evenings";
        var activeId = memory.List(deviceId).ActiveEveningId;
        var response = JsonSerializer.Serialize(memory.List(deviceId),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using (var connection = new SqliteConnection(
            $"Data Source={Path.Combine(directory, "journal.sqlite.evening.sqlite")}"))
        {
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO evening_mutations(device_id, key, route, body_hash,
                    status_code, response_json, created_at_ms)
                VALUES ($device, $key, $route, 'hash', 201, $response, $now);
                """;
            insert.Parameters.AddWithValue("$device", deviceId);
            var key = insert.Parameters.Add("$key", SqliteType.Text);
            insert.Parameters.AddWithValue("$route", route);
            insert.Parameters.AddWithValue("$response", response);
            insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            for (var index = 0; index < EveningMemoryStore.MaxMutationsPerDevice; index++)
            {
                key.Value = Guid.NewGuid().ToString("D");
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        var blocked = memory.Create(deviceId, Guid.NewGuid(), route, "new-hash", activeId, "Next");
        Check(blocked.Status == 429 && blocked.ErrorCode == "evening_mutation_limit",
            "valid idempotency keys are never evicted to make room");
        using (var connection = new SqliteConnection(
            $"Data Source={Path.Combine(directory, "journal.sqlite.evening.sqlite")}"))
        {
            connection.Open();
            using var expire = connection.CreateCommand();
            expire.CommandText = """
                UPDATE evening_mutations SET created_at_ms = $expired WHERE device_id = $device;
                """;
            expire.Parameters.AddWithValue("$device", deviceId);
            expire.Parameters.AddWithValue("$expired", DateTimeOffset.UtcNow
                .Subtract(EveningMemoryStore.MutationRetention)
                .AddMinutes(-1).ToUnixTimeMilliseconds());
            expire.ExecuteNonQuery();
        }

        Check(memory.Create(deviceId, Guid.NewGuid(), route, "new-hash", activeId, "Next").Status == 201,
            "expired mutation keys are pruned transactionally before insertion");
    }

    private static void CheckRestartReplay(string directory)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Backend:DatabasePath"] = Path.Combine(directory, "replay.sqlite"),
                ["Backend:Usage:SessionCapMicroUsd"] = "500000",
                ["Backend:Usage:DailyCapMicroUsd"] = "10000000",
                ["Backend:Usage:MonthlyCapMicroUsd"] = "10000000"
            }).Build();
        var createKey = Guid.NewGuid();
        var createBody = "create body"u8.ToArray();
        string originalId;
        string nextId;
        using (var journal = new OperationJournal(configuration, TimeProvider.System))
        {
            var memory = new EveningMemoryStore(configuration, journal);
            using var runner = new TextDialogRunner((_, _, _, _, _, _) => Task.FromResult(Reply("Test")));
            var store = new ConversationStore(TimeProvider.System, journal, runner, eveningMemory: memory);
            originalId = memory.GetStatus("replay-device").EveningId;
            var created = store.CreateEvening("replay-device", createKey, createBody, originalId, "First");
            nextId = created.Response!.ActiveEveningId;
            Check(store.ActivateEvening("replay-device", Guid.NewGuid(), "switch"u8,
                originalId, nextId).Status == 200, "switch before restart");
        }

        using (var journal = new OperationJournal(configuration, TimeProvider.System))
        {
            var memory = new EveningMemoryStore(configuration, journal);
            using var runner = new TextDialogRunner((_, _, _, _, _, _) => Task.FromResult(Reply("Test")));
            var store = new ConversationStore(TimeProvider.System, journal, runner, eveningMemory: memory);
            var replay = store.CreateEvening("replay-device", createKey, createBody, originalId, "First");
            Check(replay.Status == 201 && replay.Response!.ActiveEveningId == nextId
                && memory.GetStatus("replay-device").EveningId == originalId,
                "restart replay returns persisted snapshot without changing pointer");
            Check(store.CreateEvening("replay-device", createKey, "changed"u8,
                originalId, "First").ErrorCode == "idempotency_conflict",
                "restart preserves same-key conflict");
        }
    }

    private static void CheckEveningLimit(string directory, EveningMemoryStore memory)
    {
        const string deviceId = "catalog-cap-device";
        var activeId = memory.List(deviceId).ActiveEveningId;
        using (var connection = new SqliteConnection(
            $"Data Source={Path.Combine(directory, "journal.sqlite.evening.sqlite")}"))
        {
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO evening_catalog(device_id, evening_id, name, created_at_ms)
                VALUES ($device, $evening, 'Stored', $now);
                """;
            insert.Parameters.AddWithValue("$device", deviceId);
            var id = insert.Parameters.Add("$evening", SqliteType.Text);
            insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            for (var index = 1; index < EveningMemoryStore.MaxEvenings; index++)
            {
                id.Value = Guid.NewGuid().ToString("D");
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        Check(memory.List(deviceId).Evenings.Count == EveningMemoryStore.MaxEvenings,
            "catalog list is bounded at 100 profiles");
        var rejected = memory.Create(deviceId, Guid.NewGuid(), "/v1/evenings", "new-hash", activeId, "Extra");
        Check(rejected.Status == 429 && rejected.ErrorCode == "evening_limit",
            "catalog limit rejects a 101st evening");
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
        Check(!calls[^1].Context.Contains("sousedum"), "a claimed name does not expose another visitor session");
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
