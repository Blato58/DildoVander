using Kufr.Backend.Features.Keepsakes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

internal static class CustomerCompletionAtomicTests
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "dv-completion-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "store.sqlite");
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:DatabasePath"] = database,
                ["Keepsake:DataRoot"] = Path.Combine(root, "content"),
                ["Keepsake:Enabled"] = "true", ["Keepsake:GenerationEnabled"] = "true"
            }).Build();
            var store = new KeepsakeStore(configuration, TimeProvider.System);
            var alice = KeepsakeActor.ForAccount("alice", "alice-account-session", static () => true);
            var bob = KeepsakeActor.ForAccount("bob", "bob-account-session", static () => true);
            var issued = store.Issue(Snapshot("alice-device"), new("alice", "alice-customer-session"));
            var job = store.LeaseNext() ?? throw new Exception("Expected an admitted base job.");
            var request = store.Dispatch(job) ?? throw new Exception("Expected dispatch.");
            store.Issue(Snapshot("bob-device"), new("bob", "bob-customer-session"));
            var output = await new FakeKeepsakeProvider().GenerateAsync(request, default);
            var actor = job.KeepsakeId == issued.Id ? alice : bob;
            var before = store.ReadCompletionUpdates(actor).Items.Single(item => item.ResultLocator == job.KeepsakeId);

            Execute(database, """
                CREATE TRIGGER completion_commit_fault BEFORE INSERT ON dv10_completion_updates
                WHEN NEW.state='ready' BEGIN SELECT RAISE(ABORT,'synthetic completion commit fault'); END;
                """);
            try
            {
                store.Complete(job, output);
                throw new Exception("Completion journal failure did not abort result publication.");
            }
            catch (SqliteException error) when (error.SqliteErrorCode == 19) { }
            var rolledBack = store.ReadCompletionUpdates(actor).Items.Single(item => item.ResultLocator == job.KeepsakeId);
            Check(rolledBack.Id == before.Id && rolledBack.State != "ready",
                "event insert failure rolls back readiness publication and keeps the prior logical event");
            Check(store.Read(job.KeepsakeId, actor).Editions.All(edition => edition.JobId != job.Id),
                "no published edition survives the failed shared transaction");
            Execute(database, "DROP TRIGGER completion_commit_fault");
            Check(store.Complete(job, output), "the existing provider result can commit after the local fault is removed");
            var ready = store.ReadCompletionUpdates(actor).Items.Single(item => item.ResultLocator == job.KeepsakeId);
            Check(ready.State == "ready" && ready.Id != before.Id, "the new ready event and publication commit together");

            Execute(database, """
                CREATE TRIGGER completion_delete_fault BEFORE DELETE ON dv10_completion_updates
                BEGIN SELECT RAISE(ABORT,'synthetic completion deletion fault'); END;
                """);
            try
            {
                store.Delete(job.KeepsakeId, actor, KeepsakeDeleteScope.All, Guid.NewGuid().ToString("D"));
                throw new Exception("Completion cleanup failure did not abort the content tombstone.");
            }
            catch (SqliteException error) when (error.SqliteErrorCode == 19) { }
            Check(store.ReadCompletionUpdates(actor).Items.Any(item => item.Id == ready.Id)
                && store.Read(job.KeepsakeId, actor).Editions.Any(edition => edition.JobId == job.Id),
                "failed event cleanup rolls back the tombstone and preserves the prior accessible result");
            Execute(database, "DROP TRIGGER completion_delete_fault");

            var replacement = store.EnqueueBase(issued.Id, alice, new(), Guid.NewGuid().ToString("D"));
            KeepsakeStore.LeasedJob? lateJob = null;
            KeepsakeProviderRequest? lateRequest = null;
            while (store.LeaseNext() is { } candidate)
            {
                var candidateRequest = store.Dispatch(candidate);
                if (candidate.Id == replacement.Id)
                {
                    lateJob = candidate;
                    lateRequest = candidateRequest;
                    break;
                }
            }
            Check(lateJob is not null && lateRequest is not null, "replacement job dispatches before the deletion fence");
            var lateOutput = await new FakeKeepsakeProvider().GenerateAsync(lateRequest!, default);
            var bobIds = store.ReadCompletionUpdates(bob).Items.Select(item => item.Id).ToArray();
            store.FenceAccountDeletion("alice");
            Check(EventCount(database, "alice") == 0, "account fencing atomically purges its completion metadata");
            Check(store.ReadCompletionUpdates(bob).Items.Select(item => item.Id).SequenceEqual(bobIds),
                "another account's updates survive the account fence");
            store.Complete(lateJob!, lateOutput);
            Check(EventCount(database, "alice") == 0,
                "an already-dispatched result cannot recreate completion metadata after the account fence");
            Check(store.DeleteAccountContent("alice"), "explicit account content cleanup completes");
            Check(!store.Complete(lateJob!, lateOutput) && EventCount(database, "alice") == 0,
                "replayed provider completion cannot recreate a deleted account's completion records");
            Console.WriteLine("PASS completion transaction faults, publication rollback, deletion rollback and account cleanup isolation");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static KeepsakeIssueSnapshot Snapshot(string device) =>
        new(Guid.NewGuid().ToString("D"), device, 1, "synthetic-wand", "Synthetic wand", "Synthetic catalog", "No hardware.");

    private static void Execute(string database, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long EventCount(string database, string owner)
    {
        using var connection = new SqliteConnection("Data Source=" + database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dv10_completion_updates WHERE owner_id=$owner";
        command.Parameters.AddWithValue("$owner", owner);
        return (long)command.ExecuteScalar()!;
    }

    private static void Check(bool value, string message)
    {
        if (!value) { throw new Exception(message); }
    }
}
