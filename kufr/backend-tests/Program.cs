using System.Security.Cryptography;
using System.Text;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;

var clock = new AdjustableTimeProvider(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
var databaseDirectory = Path.Combine(Path.GetTempPath(), $"kufr-backend-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(databaseDirectory);
var journalConfiguration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Backend:DatabasePath"] = Path.Combine(databaseDirectory, "journal.sqlite")
    })
    .Build();
var journal = new OperationJournal(journalConfiguration, clock);
var conversations = new ConversationStore(clock, journal);
var bootId = Guid.NewGuid();
var request = new CreateConversationRequest("device-a", bootId, 1, "catalog-1", "cs-CZ", new ConversationConsent(false, false));
var body = Encoding.UTF8.GetBytes("create-body");
var key = Guid.NewGuid();
var created = conversations.Create("device-a", key, body, request);
Require(created.Status == 201 && created.Response is not null, "conversation creation");
var conversationId = created.Response!.ConversationId;

var turn = new TextTurnRequest(Guid.NewGuid(), bootId, 1, "private text", 63, 7, "catalog-1");
var turnBody = Encoding.UTF8.GetBytes("turn-body");
var turnKey = Guid.NewGuid();
var accepted = conversations.SubmitTurn("device-a", conversationId, turnKey, turnBody, turn);
Require(accepted.Status == 202 && accepted.OperationId is not null, "fake turn acceptance");

clock.Advance(TimeSpan.FromMinutes(15));
var replay = conversations.SubmitTurn("device-a", conversationId, turnKey, turnBody, turn);
Require(replay.OperationId == accepted.OperationId, "idempotent retry after idle expiry");
var replacement = conversations.Create("device-a", Guid.NewGuid(), body, request);
Require(replacement.Status == 201, "new conversation after idle expiry");
Require(conversations.Create("device-a", key, body, request).Response?.ConversationId == conversationId,
    "create retry retained for 24 hours");

var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
var configuration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Devices:0:DeviceId"] = "device-a",
        ["Devices:0:CredentialSha256"] = hash
    })
    .Build();
var access = new DeviceAccessStore(configuration, clock);
Require(access.VerifyCredential(secret) == "device-a", "provisioned credential");
var issued = access.IssueToken("device-a");
Require(issued is not null && access.VerifyAccessToken(issued.AccessToken) == "device-a", "issued token");
clock.Advance(TimeSpan.FromMinutes(15));
Require(access.VerifyAccessToken(issued!.AccessToken) is null, "token expiry");

var pendingCorrelation = new TurnCorrelation("device-a", bootId, conversationId, Guid.NewGuid(), 1, 7);
var pending = journal.Reserve("device-a", "/v1/conversations/pending/turns", Guid.NewGuid(), "pending-hash", pendingCorrelation);
var concurrentKey = Guid.NewGuid();
var concurrentCorrelation = pendingCorrelation with { ClientTurnId = Guid.NewGuid() };
var concurrentReservations = await Task.WhenAll(
    Task.Run(() => journal.Reserve("device-a", "concurrent", concurrentKey, "same-hash", concurrentCorrelation)),
    Task.Run(() => journal.Reserve("device-a", "concurrent", concurrentKey, "same-hash", concurrentCorrelation)));
Require(concurrentReservations[0].OperationId == concurrentReservations[1].OperationId,
    "atomic concurrent idempotency");
Require(concurrentReservations.Count(value => !value.Existing) == 1,
    "one concurrent reservation");
Require(journal.Reserve("device-a", "concurrent", concurrentKey, "different-hash", concurrentCorrelation).Conflict,
    "same key with different body");

try
{
    _ = new OperationJournal(journalConfiguration, clock);
    throw new Exception("Second live database owner was accepted.");
}
catch (InvalidOperationException)
{
    // Startup recovery is allowed only for the single database owner.
}

journal.Dispose();
var restartedJournal = new OperationJournal(journalConfiguration, clock);
var restartedStore = new ConversationStore(clock, restartedJournal);
var replayAfterRestart = restartedStore.SubmitTurn("device-a", conversationId, turnKey, turnBody, turn);
Require(replayAfterRestart.OperationId == accepted.OperationId, "idempotent retry after restart");
var lostResult = restartedStore.GetOperation("device-a", accepted.OperationId!);
Require(lostResult.Status == 200 && lostResult.Operation?.Status == "failed", "lost result after restart");
Require(restartedStore.GetOperation("other-device", accepted.OperationId!).Status == 403,
    "journaled operation device scope");
Require(restartedJournal.GetOperation("device-a", pending.OperationId).Operation?.Status == "unknown",
    "pending reservation becomes unknown on restart");
AssertNoVisitorText(Path.Combine(databaseDirectory, "journal.sqlite"), "private text");

var liveConversation = restartedStore.Create("device-a", Guid.NewGuid(), body, request);
Require(liveConversation.Response is not null, "new session after restart");
var liveTurn = restartedStore.SubmitTurn(
    "device-a",
    liveConversation.Response!.ConversationId,
    Guid.NewGuid(),
    turnBody,
    turn with { ClientTurnId = Guid.NewGuid() });
Require(liveTurn.OperationId is not null
    && restartedStore.GetOperation("device-a", liveTurn.OperationId).Operation?.Status == "completed",
    "live fake result before expiry");
clock.Advance(TimeSpan.FromHours(24));
Require(restartedStore.GetOperation("device-a", liveTurn.OperationId!).Status == 404,
    "cached fake result expires with journal");

var duplicateConfiguration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Devices:0:DeviceId"] = "device-a",
        ["Devices:0:CredentialSha256"] = hash,
        ["Devices:1:DeviceId"] = "device-b",
        ["Devices:1:CredentialSha256"] = hash
    })
    .Build();
try
{
    _ = new DeviceAccessStore(duplicateConfiguration, clock);
    throw new Exception("Duplicate credential hash was accepted.");
}
catch (InvalidOperationException)
{
    // A shared credential must not select a device by configuration order.
}

Console.WriteLine("Backend clock tests: expiry, restart, SQLite concurrency and provisioning passed");
restartedJournal.Dispose();
SqliteConnection.ClearAllPools();
Directory.Delete(databaseDirectory, recursive: true);

static void Require(bool condition, string name)
{
    if (!condition)
    {
        throw new Exception($"Failed: {name}");
    }
}

static void AssertNoVisitorText(string databasePath, string text)
{
    using var connection = new SqliteConnection($"Data Source={databasePath}");
    connection.Open();

    foreach (var table in new[] { "turn_operations", "turn_idempotency" })
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table}";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            for (var column = 0; column < reader.FieldCount; column++)
            {
                Require(!reader.GetValue(column).ToString()!.Contains(text, StringComparison.Ordinal),
                    "visitor text absent from SQLite");
            }
        }
    }
}

sealed class AdjustableTimeProvider(DateTimeOffset current) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => current;

    public void Advance(TimeSpan duration)
    {
        current += duration;
    }
}
