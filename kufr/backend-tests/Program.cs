using System.Security.Cryptography;
using System.Text;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

// Paid provider diagnostic is explicit and never part of normal offline CI.
if (args is ["--show"])
{
    ShowDirectorTests.Run();
    OperatorOverrideTests.Run();
    await ShowDeviceCueTests.RunAsync();
    await ShowSpeechProviderTests.RunAsync();
    ChronicleTests.Run();
    OfflineOutcomeTests.Run();
    await DuelTests.RunAsync();
    await KeepsakeCoreTests.RunAsync();
    await EveningMemoryTests.RunAsync();
    return;
}
if (args is ["--duel"]) { await DuelTests.RunAsync(); return; }
if (args is ["--show-speech"]) { await ShowSpeechProviderTests.RunAsync(); return; }
if (args is ["--keepsake-fixture"]) { KeepsakeHttpFixture.Run(); return; }
if (args is ["--keepsake"]) { KeepsakeOutcomeJournalTests.Run(); await KeepsakeCoreTests.RunAsync(); return; }
if (args is ["--twin"]) { await TwinTests.RunAsync(); return; }
if (args is ["--twin-media"]) { await TwinMediaTests.RunAsync(); return; }
if (args is ["--character"])
{
    CharacterDirectorTests.Run();
    CharacterOperationTests.Run();
    await AutonomousLiveTests.RunCharacterAsync();
    await AutonomousLiveTests.RunAsync();
    ServerModeTests.Run();
    return;
}
if (args is ["--firmware"]) { FirmwareReleaseTests.Run(); return; }
if (args is ["--conversation-engines"]) { await RealtimeTests.RunAsync(); await GptLiveTests.RunAsync(); return; }
if (args is ["--memory"]) { await HybridMemoryTests.RunAsync(); await EveningMemoryTests.RunAsync(); return; }
if (args is ["--gpt-live-smoke"])
{
    Environment.ExitCode = await GptLiveSmoke.RunAsync();
    return;
}
if (args is ["--autonomous-memory"])
{
    await AutonomousLiveTests.RunAsync();
    await HybridMemoryTests.RunAsync();
    return;
}

if (args is ["--image"]) { await CostumeImageTests.RunAsync(); return; }
ShowDirectorTests.Run();
OperatorOverrideTests.Run();
await ShowDeviceCueTests.RunAsync();
await ShowSpeechProviderTests.RunAsync();
ChronicleTests.Run();
OfflineOutcomeTests.Run();
await DuelTests.RunAsync();
await KeepsakeCoreTests.RunAsync();
KeepsakeOutcomeJournalTests.Run();
FirmwareReleaseTests.Run();
CharacterDirectorTests.Run();
CharacterOperationTests.Run();
await AutonomousLiveTests.RunCharacterAsync();
await CostumeImageTests.RunAsync();
await RealtimeTests.RunAsync();
await GptLiveTests.RunAsync();
await PersonalityTests.RunAsync();
await AutonomousLiveTests.RunAsync();
await HybridMemoryTests.RunAsync();
ResponsesTextDialogBudgetTests.Run();
await TextDialogRuntimeTests.RunAsync();
await EveningMemoryTests.RunAsync();
await VoiceProviderTests.RunAsync();
ServerModeTests.Run();
ServerLogTests.Run();

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
Require(conversations.GetOperation("device-a", accepted.OperationId!).Operation?.Status == "completed",
    "fake result available during live session");

var audioRequest = request with
{
    DeviceId = "device-c",
    Consent = new ConversationConsent(true, false)
};
var audioSession = conversations.Create("device-c", Guid.NewGuid(), body, audioRequest);
Require(audioSession.Response is not null, "audio-consented session created");
var audioUpload = new TranscriptionUpload(
    Guid.NewGuid(), bootId, 1, 7, Encoding.UTF8.GetBytes("private audio payload"));
var transcriptionKey = Guid.NewGuid();
var transcription = conversations.SubmitTranscription(
    "device-c", audioSession.Response!.ConversationId, transcriptionKey, audioUpload);
Require(transcription.Status == 202 && transcription.OperationId is not null,
    "fake transcription accepted");
Require(conversations.GetOperation("device-c", transcription.OperationId!).Operation?.Kind == "transcription",
    "transcription kind and result retained in RAM");
Require(conversations.SubmitTranscription(
    "device-c", audioSession.Response.ConversationId, transcriptionKey, audioUpload).OperationId
    == transcription.OperationId, "transcription retry");
Require(conversations.SubmitTranscription(
    "device-c", audioSession.Response.ConversationId, transcriptionKey,
    audioUpload with { DeviceRevision = 8 }).Status == 409,
    "transcription retry body conflict");
var audioTurnId = Guid.NewGuid();
var audioTurn = conversations.SubmitTurn(
    "device-c", audioSession.Response.ConversationId, Guid.NewGuid(), turnBody,
    turn with { ClientTurnId = audioTurnId });
Require(audioTurn.Status == 202, "speech source turn accepted");
var speechBody = Encoding.UTF8.GetBytes("speech-device");
var speechKey = Guid.NewGuid();
var speech = conversations.SubmitSpeech(
    "device-c", audioSession.Response.ConversationId, speechKey, speechBody,
    new SpeechRequest(audioTurnId, "device"));
Require(speech.Status == 202 && speech.OperationId is not null, "fake speech accepted");
var speechResult = conversations.GetOperation("device-c", speech.OperationId!).Operation?.Result as SpeechResult;
Require(speechResult is { OutputTarget: "device" }, "speech result target");
Require(conversations.GetAudio("device-c", speechResult!.AudioId).Wav is { Length: 6444 },
    "valid short fake WAV available");
Require(conversations.GetAudio("other-device", speechResult.AudioId).Status == 403,
    "foreign audio denied");
Require(conversations.SubmitSpeech(
    "device-c", audioSession.Response.ConversationId, Guid.NewGuid(), speechBody,
    new SpeechRequest(audioTurnId, "phone")).Status == 409,
    "only one output target while audio active");
Require(conversations.SubmitSpeech(
    "device-c", audioSession.Response.ConversationId, speechKey, speechBody,
    new SpeechRequest(audioTurnId, "device")).OperationId == speech.OperationId,
    "speech retry preserves operation");
clock.Advance(TimeSpan.FromSeconds(121));
Require(conversations.GetAudio("device-c", speechResult.AudioId).Status == 410,
    "audio expires after 120 seconds");
var nextSpeech = conversations.SubmitSpeech(
    "device-c", audioSession.Response.ConversationId, Guid.NewGuid(),
    Encoding.UTF8.GetBytes("speech-phone"), new SpeechRequest(audioTurnId, "phone"));
Require(nextSpeech.Status == 202 && nextSpeech.OperationId is not null,
    "speech can resume after audio expiry");
var nextAudioId = ((SpeechResult)conversations.GetOperation("device-c", nextSpeech.OperationId!).Operation!.Result!).AudioId;
Require(conversations.CancelOperation("device-c", nextSpeech.OperationId!, Guid.NewGuid()).Status == 204,
    "speech cancellation recorded");
Require(conversations.GetAudio("device-c", nextAudioId).Status == 404,
    "speech cancellation removes audio");
var finalSpeech = conversations.SubmitSpeech(
    "device-c", audioSession.Response.ConversationId, Guid.NewGuid(),
    Encoding.UTF8.GetBytes("speech-final"), new SpeechRequest(audioTurnId, "device"));
Require(finalSpeech.Status == 202, "speech after cancellation accepted");
var finalAudioId = ((SpeechResult)conversations.GetOperation("device-c", finalSpeech.OperationId!).Operation!.Result!).AudioId;
Require(conversations.EndConversation("device-c", audioSession.Response.ConversationId, Guid.NewGuid()).Status == 204,
    "audio session ended");
Require(conversations.GetAudio("device-c", finalAudioId).Status == 404,
    "conversation end removes audio");

clock.Advance(TimeSpan.FromMinutes(15));
conversations.SweepExpired();
Require(conversations.GetOperation("device-a", accepted.OperationId!).Operation?.Status == "failed",
    "fake result removed at idle expiry");
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
var cancelled = journal.Reserve("device-a", "cancelled", Guid.NewGuid(), "cancel-hash", pendingCorrelation);
var cancellationKey = Guid.NewGuid();
Require(journal.CancelOperation("other-device", cancelled.OperationId, cancellationKey).Status == 403,
    "foreign operation cancellation rejected");
Require(journal.CancelOperation("device-a", cancelled.OperationId, cancellationKey).Status == 204,
    "owner can cancel operation");
Require(!journal.MarkCompleted(cancelled.OperationId), "late completion cannot override cancellation");
Require(journal.GetOperation("device-a", cancelled.OperationId).Operation?.Status == "cancelled",
    "cancelled operation remains cancelled");
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

var quotaConversationId = Guid.NewGuid().ToString("D");
var quotaCorrelation = pendingCorrelation with { ConversationId = quotaConversationId };
var quotaKeys = new List<Guid>();
for (var index = 0; index < 9; index++)
{
    var quotaKey = Guid.NewGuid();
    quotaKeys.Add(quotaKey);
    var reservation = journal.Reserve(
        "device-a",
        "quota",
        quotaKey,
        "quota-body",
        quotaCorrelation with { ClientTurnId = Guid.NewGuid() });
    Require(!reservation.LimitExceeded, "first nine turn reservations");
}

var quotaBoundary = await Task.WhenAll(
    Task.Run(() => journal.Reserve(
        "device-a", "quota", Guid.NewGuid(), "quota-body",
        quotaCorrelation with { ClientTurnId = Guid.NewGuid() })),
    Task.Run(() => journal.Reserve(
        "device-a", "quota", Guid.NewGuid(), "quota-body",
        quotaCorrelation with { ClientTurnId = Guid.NewGuid() })));
Require(quotaBoundary.Count(value => value.LimitExceeded) == 1,
    "one of two concurrent reservations at tenth turn is rejected");
Require(journal.Reserve("device-a", "quota", Guid.NewGuid(), "quota-body", quotaCorrelation).LimitExceeded,
    "eleventh turn reservation rejected");
Require(journal.Reserve("device-a", "quota", quotaKeys[0], "quota-body", quotaCorrelation).Existing,
    "idempotent retry allowed at quota limit");

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
Require(restartedJournal.CancelOperation("device-a", cancelled.OperationId, cancellationKey).Status == 204,
    "cancellation retry survives restart");
Require(restartedStore.Create("device-a", key, body, request).Response?.ConversationId == conversationId,
    "create idempotency survives restart");
Require(restartedStore.Create("device-a", key, Encoding.UTF8.GetBytes("changed-body"), request).Status == 409,
    "create body mismatch survives restart");
Require(restartedJournal.Reserve("device-a", "quota", Guid.NewGuid(), "quota-body", quotaCorrelation).LimitExceeded,
    "turn reservation limit survives restart");
var replayAfterRestart = restartedStore.SubmitTurn("device-a", conversationId, turnKey, turnBody, turn);
Require(replayAfterRestart.OperationId == accepted.OperationId, "idempotent retry after restart");
var lostResult = restartedStore.GetOperation("device-a", accepted.OperationId!);
Require(lostResult.Status == 200 && lostResult.Operation?.Status == "failed", "lost result after restart");
Require(restartedStore.GetOperation("other-device", accepted.OperationId!).Status == 403,
    "journaled operation device scope");
Require(restartedJournal.GetOperation("device-a", pending.OperationId).Operation?.Status == "unknown",
    "pending reservation becomes unknown on restart");
AssertNoVisitorText(Path.Combine(databaseDirectory, "journal.sqlite"), "private text");
AssertNoVisitorText(Path.Combine(databaseDirectory, "journal.sqlite"), "private audio payload");
Require(restartedStore.GetOperation("device-c", transcription.OperationId!).Operation is
    { Kind: "transcription", Status: "cancelled" },
    "conversation end keeps transcription cancelled after restart");

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
restartedJournal.PurgeExpired();
using (var retainedConnection = new SqliteConnection($"Data Source={Path.Combine(databaseDirectory, "journal.sqlite")}"))
{
    retainedConnection.Open();
    foreach (var table in new[]
    {
        "turn_operations",
        "turn_idempotency",
        "conversation_turn_usage",
        "conversation_creates",
        "mutation_idempotency"
    })
    {
        using var count = retainedConnection.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM {table}";
        Require((long)count.ExecuteScalar()! == 0, $"expired {table} removed");
    }
}

var maxLifeRequest = request with { DeviceId = "device-b" };
var maxLifeSession = restartedStore.Create("device-b", Guid.NewGuid(), body, maxLifeRequest);
Require(maxLifeSession.Response is not null, "absolute lifetime session created");
clock.Advance(TimeSpan.FromMinutes(14));
Require(restartedStore.SubmitTurn(
    "device-b", maxLifeSession.Response!.ConversationId, Guid.NewGuid(), turnBody,
    turn with { ClientTurnId = Guid.NewGuid() }).Status == 202,
    "turn before first idle deadline");
clock.Advance(TimeSpan.FromMinutes(14));
Require(restartedStore.SubmitTurn(
    "device-b", maxLifeSession.Response.ConversationId, Guid.NewGuid(), turnBody,
    turn with { ClientTurnId = Guid.NewGuid() }).Status == 202,
    "turn before absolute deadline");
clock.Advance(TimeSpan.FromMinutes(2));
restartedStore.SweepExpired();
Require(restartedStore.SubmitTurn(
    "device-b", maxLifeSession.Response.ConversationId, Guid.NewGuid(), turnBody,
    turn with { ClientTurnId = Guid.NewGuid() }).Status == 404,
    "conversation cannot exceed 30 minutes");

var migrationPath = Path.Combine(databaseDirectory, "legacy-v1.sqlite");
var legacyOperationId = Guid.NewGuid().ToString("D");
var legacyKey = Guid.NewGuid();
var legacyConversationId = Guid.NewGuid().ToString("D");
using (var legacyConnection = new SqliteConnection($"Data Source={migrationPath}"))
{
    legacyConnection.Open();
    using var legacySchema = legacyConnection.CreateCommand();
    legacySchema.CommandText = """
        CREATE TABLE turn_operations (
            operation_id TEXT PRIMARY KEY, device_id TEXT NOT NULL,
            conversation_id TEXT NOT NULL, boot_id TEXT NOT NULL,
            client_turn_id TEXT NOT NULL, generation INTEGER NOT NULL,
            device_revision INTEGER NOT NULL, status TEXT NOT NULL,
            expires_at_ms INTEGER NOT NULL
        );
        CREATE TABLE turn_idempotency (
            device_id TEXT NOT NULL, route TEXT NOT NULL, key TEXT NOT NULL,
            body_hash TEXT NOT NULL, operation_id TEXT NOT NULL,
            expires_at_ms INTEGER NOT NULL,
            PRIMARY KEY (device_id, route, key)
        );
        PRAGMA user_version=1;
        """;
    legacySchema.ExecuteNonQuery();
    using var legacyRows = legacyConnection.CreateCommand();
    legacyRows.CommandText = """
        INSERT INTO turn_operations
            (operation_id, device_id, conversation_id, boot_id, client_turn_id,
             generation, device_revision, status, expires_at_ms)
        VALUES ($operation, 'device-a', $conversation, $boot, $turn, 1, 7, 'completed', $expires);
        INSERT INTO turn_idempotency
            (device_id, route, key, body_hash, operation_id, expires_at_ms)
        VALUES ('device-a', 'legacy', $key, 'legacy-hash', $operation, $expires);
        """;
    legacyRows.Parameters.AddWithValue("$operation", legacyOperationId);
    legacyRows.Parameters.AddWithValue("$conversation", legacyConversationId);
    legacyRows.Parameters.AddWithValue("$boot", bootId.ToString("D"));
    legacyRows.Parameters.AddWithValue("$turn", Guid.NewGuid().ToString("D"));
    legacyRows.Parameters.AddWithValue("$expires", clock.GetUtcNow().AddHours(24).ToUnixTimeMilliseconds());
    legacyRows.Parameters.AddWithValue("$key", legacyKey.ToString("D"));
    legacyRows.ExecuteNonQuery();
}

var migrationConfiguration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?> { ["Backend:DatabasePath"] = migrationPath })
    .Build();
using (var migratedJournal = new OperationJournal(migrationConfiguration, clock))
{
    Require(migratedJournal.FindExisting("device-a", "legacy", legacyKey, "legacy-hash")?.OperationId
        == legacyOperationId, "v1 idempotency row preserved");
    Require(migratedJournal.GetOperation("device-a", legacyOperationId).Operation?.Status == "failed",
        "v1 completed operation preserved without sensitive result");
    Require(!migratedJournal.Reserve(
        "device-a",
        "migrated",
        Guid.NewGuid(),
        "hash",
        quotaCorrelation with { ConversationId = Guid.NewGuid().ToString("D") }).LimitExceeded,
        "v1 schema supports v2 quota reservation");
}
using (var migratedConnection = new SqliteConnection($"Data Source={migrationPath}"))
{
    migratedConnection.Open();
    using var version = migratedConnection.CreateCommand();
    version.CommandText = "PRAGMA user_version";
    Require((long)version.ExecuteScalar()! == 7, "schema migrated from v1 to v7");
}

var versionTwoPath = Path.Combine(databaseDirectory, "legacy-v2.sqlite");
var versionTwoConversationId = Guid.NewGuid().ToString("D");
using (var versionTwoConnection = new SqliteConnection($"Data Source={versionTwoPath}"))
{
    versionTwoConnection.Open();
    using var versionTwoSchema = versionTwoConnection.CreateCommand();
    versionTwoSchema.CommandText = """
        CREATE TABLE conversation_turn_usage (
            conversation_id TEXT PRIMARY KEY,
            device_id TEXT NOT NULL,
            reserved_turns INTEGER NOT NULL CHECK (reserved_turns BETWEEN 0 AND 10),
            expires_at_ms INTEGER NOT NULL
        );
        INSERT INTO conversation_turn_usage
            (conversation_id, device_id, reserved_turns, expires_at_ms)
        VALUES ($conversation, 'device-a', 10, $expires);
        PRAGMA user_version=2;
        """;
    versionTwoSchema.Parameters.AddWithValue("$conversation", versionTwoConversationId);
    versionTwoSchema.Parameters.AddWithValue("$expires", clock.GetUtcNow().AddHours(24).ToUnixTimeMilliseconds());
    versionTwoSchema.ExecuteNonQuery();
}
var versionTwoConfiguration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?> { ["Backend:DatabasePath"] = versionTwoPath })
    .Build();
using (var versionFourJournal = new OperationJournal(versionTwoConfiguration, clock))
{
    Require(versionFourJournal.Reserve(
        "device-a", "quota", Guid.NewGuid(), "hash",
        quotaCorrelation with { ConversationId = versionTwoConversationId }).LimitExceeded,
        "populated v2 quota survives v4 migration");
    Require(versionFourJournal.ReserveCreate("device-a", Guid.NewGuid(), "hash").Response is not null,
        "create table available after v2 migration");
}

var versionThreePath = Path.Combine(databaseDirectory, "legacy-v3.sqlite");
var versionThreeKey = Guid.NewGuid();
var versionThreeConversationId = Guid.NewGuid().ToString("D");
using (var versionThreeConnection = new SqliteConnection($"Data Source={versionThreePath}"))
{
    versionThreeConnection.Open();
    using var versionThreeSchema = versionThreeConnection.CreateCommand();
    versionThreeSchema.CommandText = """
        CREATE TABLE conversation_creates (
            device_id TEXT NOT NULL, key TEXT NOT NULL, body_hash TEXT NOT NULL,
            conversation_id TEXT NOT NULL, response_expires_at_ms INTEGER NOT NULL,
            record_expires_at_ms INTEGER NOT NULL,
            PRIMARY KEY (device_id, key)
        );
        INSERT INTO conversation_creates
            (device_id, key, body_hash, conversation_id,
             response_expires_at_ms, record_expires_at_ms)
        VALUES ('device-a', $key, 'legacy-hash', $conversation, $expires, $expires);
        PRAGMA user_version=3;
        """;
    versionThreeSchema.Parameters.AddWithValue("$key", versionThreeKey.ToString("D"));
    versionThreeSchema.Parameters.AddWithValue("$conversation", versionThreeConversationId);
    versionThreeSchema.Parameters.AddWithValue("$expires", clock.GetUtcNow().AddHours(24).ToUnixTimeMilliseconds());
    versionThreeSchema.ExecuteNonQuery();
}

var versionThreeConfiguration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?> { ["Backend:DatabasePath"] = versionThreePath })
    .Build();
using (var versionFourJournal = new OperationJournal(versionThreeConfiguration, clock))
{
    Require(versionFourJournal.FindExistingCreate("device-a", versionThreeKey, "legacy-hash")?
        .Response?.ConversationId == versionThreeConversationId,
        "populated v3 create survives v4 migration");
    Require(versionFourJournal.CancelOperation("device-a", "missing", Guid.NewGuid()).Status == 404,
        "v4 mutation table available after v3 migration");
}

var versionFourPath = Path.Combine(databaseDirectory, "legacy-v4.sqlite");
var versionFourOperationId = Guid.NewGuid().ToString("D");
using (var versionFourConnection = new SqliteConnection($"Data Source={versionFourPath}"))
{
    versionFourConnection.Open();
    using var versionFourSchema = versionFourConnection.CreateCommand();
    versionFourSchema.CommandText = """
        CREATE TABLE turn_operations (
            operation_id TEXT PRIMARY KEY, device_id TEXT NOT NULL,
            conversation_id TEXT NOT NULL, boot_id TEXT NOT NULL,
            client_turn_id TEXT NOT NULL, generation INTEGER NOT NULL,
            device_revision INTEGER NOT NULL, status TEXT NOT NULL,
            expires_at_ms INTEGER NOT NULL
        );
        INSERT INTO turn_operations
            (operation_id, device_id, conversation_id, boot_id, client_turn_id,
             generation, device_revision, status, expires_at_ms)
        VALUES ($operation, 'device-a', $conversation, $boot, $turn, 1, 7, 'completed', $expires);
        PRAGMA user_version=4;
        """;
    versionFourSchema.Parameters.AddWithValue("$operation", versionFourOperationId);
    versionFourSchema.Parameters.AddWithValue("$conversation", Guid.NewGuid().ToString("D"));
    versionFourSchema.Parameters.AddWithValue("$boot", bootId.ToString("D"));
    versionFourSchema.Parameters.AddWithValue("$turn", Guid.NewGuid().ToString("D"));
    versionFourSchema.Parameters.AddWithValue("$expires", clock.GetUtcNow().AddHours(24).ToUnixTimeMilliseconds());
    versionFourSchema.ExecuteNonQuery();
}

var versionFourConfiguration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?> { ["Backend:DatabasePath"] = versionFourPath })
    .Build();
using (var versionFiveJournal = new OperationJournal(versionFourConfiguration, clock))
{
    Require(versionFiveJournal.GetOperation("device-a", versionFourOperationId).Operation is
        { Kind: "turn", Status: "failed" },
        "populated v4 turn becomes v6 turn with result_expired");
}

var versionFivePath = Path.Combine(databaseDirectory, "legacy-v5.sqlite");
var versionFiveOperationId = Guid.NewGuid().ToString("D");
using (var versionFiveConnection = new SqliteConnection($"Data Source={versionFivePath}"))
{
    versionFiveConnection.Open();
    using var versionFiveSchema = versionFiveConnection.CreateCommand();
    versionFiveSchema.CommandText = """
        CREATE TABLE turn_operations (
            operation_id TEXT PRIMARY KEY, device_id TEXT NOT NULL,
            conversation_id TEXT NOT NULL, boot_id TEXT NOT NULL,
            client_turn_id TEXT NOT NULL, generation INTEGER NOT NULL,
            device_revision INTEGER NOT NULL, status TEXT NOT NULL,
            expires_at_ms INTEGER NOT NULL, kind TEXT NOT NULL
        );
        INSERT INTO turn_operations
            (operation_id, device_id, conversation_id, boot_id, client_turn_id,
             generation, device_revision, status, expires_at_ms, kind)
        VALUES ($operation, 'device-a', $conversation, $boot, $turn,
                1, 7, 'completed', $expires, 'transcription');
        PRAGMA user_version=5;
        """;
    versionFiveSchema.Parameters.AddWithValue("$operation", versionFiveOperationId);
    versionFiveSchema.Parameters.AddWithValue("$conversation", Guid.NewGuid().ToString("D"));
    versionFiveSchema.Parameters.AddWithValue("$boot", bootId.ToString("D"));
    versionFiveSchema.Parameters.AddWithValue("$turn", Guid.NewGuid().ToString("D"));
    versionFiveSchema.Parameters.AddWithValue("$expires", clock.GetUtcNow().AddHours(24).ToUnixTimeMilliseconds());
    versionFiveSchema.ExecuteNonQuery();
}

var versionFiveConfiguration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?> { ["Backend:DatabasePath"] = versionFivePath })
    .Build();
using (var versionSixJournal = new OperationJournal(versionFiveConfiguration, clock))
{
    Require(versionSixJournal.GetOperation("device-a", versionFiveOperationId).Operation is
        { Kind: "transcription", Status: "failed" },
        "populated v5 operation kind survives v6 migration");
}

Require(journalConfiguration["Backend:Usage:SessionCapMicroUsd"] is null,
    "fake fixture has no paid usage cap");
var noBudgetReservation = restartedJournal.Reserve(
    "device-a", "paid-without-caps", Guid.NewGuid(), "hash",
    quotaCorrelation with { ConversationId = Guid.NewGuid().ToString("D") },
    consumesTurn: false,
    estimatedCostMicroUsd: 1);
Require(!noBudgetReservation.CostRejected, "missing cap settings do not block accounting");

var costClock = new AdjustableTimeProvider(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
var costPath = Path.Combine(databaseDirectory, "cost.sqlite");
var costConfiguration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Backend:DatabasePath"] = costPath,
        ["Backend:Usage:SessionCapMicroUsd"] = "100",
        ["Backend:Usage:DailyCapMicroUsd"] = "100",
        ["Backend:Usage:MonthlyCapMicroUsd"] = "100"
    })
    .Build();
var costConversationId = Guid.NewGuid().ToString("D");
var costCorrelation = pendingCorrelation with { ConversationId = costConversationId };
var costRoute = "paid-cost-fixture";
var costKey = Guid.NewGuid();
string acceptedCostOperation;
string runningCostOperation;
using (var costJournal = new OperationJournal(costConfiguration, costClock))
{
    var competitors = await Task.WhenAll(
        Task.Run(() => costJournal.Reserve(
            "device-a", costRoute, costKey, "same-body", costCorrelation,
            consumesTurn: false, estimatedCostMicroUsd: 60)),
        Task.Run(() => costJournal.Reserve(
            "device-a", costRoute, Guid.NewGuid(), "other-body", costCorrelation,
            consumesTurn: false, estimatedCostMicroUsd: 60)));
    Require(competitors.Count(value => !value.CostRejected) == 2,
        "parallel accounting reservations ignore legacy caps");
    acceptedCostOperation = competitors[0].OperationId;
    Require(costJournal.SettleCost(acceptedCostOperation, 30), "actual charge settles below estimate");
    Require(costJournal.SettleCost(acceptedCostOperation, 30), "same settlement is idempotent");
    Require(!costJournal.SettleCost(acceptedCostOperation, 31), "different settlement conflicts");
    var secondCostKey = Guid.NewGuid();
    var secondCost = costJournal.Reserve(
        "device-a", costRoute, secondCostKey, "third-body", costCorrelation,
        consumesTurn: false, estimatedCostMicroUsd: 60);
    Require(!secondCost.CostRejected, "settlement adjusts audit accounting");
    runningCostOperation = secondCost.OperationId;
    Require(costJournal.MarkRunning(runningCostOperation),
        "reserved paid operation enters running state once");
    Require(!costJournal.MarkRunning(runningCostOperation),
        "running operation cannot be started a second time");
    var secondCostRetry = costJournal.Reserve(
        "device-a", costRoute, secondCostKey, "third-body", costCorrelation,
        consumesTurn: false, estimatedCostMicroUsd: 60);
    Require(secondCostRetry.Existing && secondCostRetry.OperationId == secondCost.OperationId,
        "paid retry returns one operation without charging again");
    Require(costJournal.Reserve(
        "device-a", costRoute, secondCostKey, "changed-body", costCorrelation,
        consumesTurn: false, estimatedCostMicroUsd: 60).Conflict,
        "paid key rejects changed body");
    Require(!costJournal.Reserve(
        "device-a", costRoute, Guid.NewGuid(), "fourth-body", costCorrelation,
        consumesTurn: false, estimatedCostMicroUsd: 11).CostRejected,
        "legacy remaining monetary cap never blocks a reservation");

    var overageCorrelation = costCorrelation with
    {
        DeviceId = "device-b",
        ConversationId = Guid.NewGuid().ToString("D")
    };
    Require(!costJournal.Reserve(
        "device-b", costRoute, Guid.NewGuid(), "other-device-over-cap", overageCorrelation,
        consumesTurn: false, estimatedCostMicroUsd: 11).CostRejected,
        "period totals do not block an independent session");
    var overage = costJournal.Reserve(
        "device-b", costRoute, Guid.NewGuid(), "overage-body", overageCorrelation,
        consumesTurn: false, estimatedCostMicroUsd: 10);
    Require(!overage.CostRejected && costJournal.SettleCost(overage.OperationId, 120),
        "actual overage remains charged instead of being hidden");
    Require(!costJournal.Reserve(
        "device-b", costRoute, Guid.NewGuid(), "overage-next", overageCorrelation,
        consumesTurn: false, estimatedCostMicroUsd: 1).CostRejected,
        "actual overage is accounted and never blocks later work");
}
var partialCostConfiguration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Backend:DatabasePath"] = Path.Combine(databaseDirectory, "partial-cost.sqlite"),
        ["Backend:Usage:SessionCapMicroUsd"] = "100"
    })
    .Build();
using (var sessionOnlyJournal = new OperationJournal(partialCostConfiguration, costClock))
{
    Require(!sessionOnlyJournal.Reserve("device-c", costRoute, Guid.NewGuid(), "session-only", costCorrelation with
        { DeviceId = "device-c", ConversationId = Guid.NewGuid().ToString("D") },
        consumesTurn: false, estimatedCostMicroUsd: 100).CostRejected,
        "session-only configuration needs no period caps");
}
using (var restartedCostJournal = new OperationJournal(costConfiguration, costClock))
{
    Require(restartedCostJournal.GetOperation("device-a", acceptedCostOperation).Operation?.Status == "unknown",
        "paid pending operation remains unknown after restart");
    Require(restartedCostJournal.GetOperation("device-a", runningCostOperation).Operation?.Status == "unknown",
        "paid running operation remains unknown after restart");
    Require(!restartedCostJournal.MarkRunning(runningCostOperation),
        "unknown operation cannot restart its provider call");
    Require(!restartedCostJournal.Reserve(
        "device-a", costRoute, Guid.NewGuid(), "after-restart", costCorrelation,
        consumesTurn: false, estimatedCostMicroUsd: 11).CostRejected,
        "unknown accounting reservations do not block work after restart");
    costClock.Advance(TimeSpan.FromDays(8));
    restartedCostJournal.PurgeExpired();
    using var costConnection = new SqliteConnection($"Data Source={costPath}");
    costConnection.Open();
    using var costCount = costConnection.CreateCommand();
    costCount.CommandText = "SELECT COUNT(*) FROM cost_reservations";
    Require((long)costCount.ExecuteScalar()! == 0, "seven-day technical cost audit expires");
    costCount.CommandText = "SELECT COUNT(*) FROM cost_period_usage";
    Require((long)costCount.ExecuteScalar()! > 0, "daily/monthly aggregate retained after audit expiry");
    costClock.Advance(TimeSpan.FromDays(91));
    restartedCostJournal.PurgeExpired();
    Require((long)costCount.ExecuteScalar()! == 0, "ninety-day aggregate retention expires");
}

var speechClock = new AdjustableTimeProvider(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
var speechConfiguration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Backend:DatabasePath"] = Path.Combine(databaseDirectory, "speech-idle.sqlite")
    })
    .Build();
using (var speechJournal = new OperationJournal(speechConfiguration, speechClock))
{
    var speechStore = new ConversationStore(speechClock, speechJournal);
    var speechSession = speechStore.Create(
        "device-a", Guid.NewGuid(), body, request with { Consent = new ConversationConsent(true, false) });
    Require(speechSession.Response is not null, "speech idle fixture session");
    var speechTurnId = Guid.NewGuid();
    Require(speechStore.SubmitTurn(
        "device-a", speechSession.Response!.ConversationId, Guid.NewGuid(), turnBody,
        turn with { ClientTurnId = speechTurnId }).Status == 202, "speech idle fixture turn");
    speechClock.Advance(TimeSpan.FromMinutes(14));
    Require(speechStore.SubmitSpeech(
        "device-a", speechSession.Response.ConversationId, Guid.NewGuid(), speechBody,
        new SpeechRequest(speechTurnId, "device")).Status == 202,
        "speech near original idle deadline");
    speechClock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
    Require(speechStore.SubmitSpeech(
        "device-a", speechSession.Response.ConversationId, Guid.NewGuid(), speechBody,
        new SpeechRequest(speechTurnId, "phone")).Status == 202,
        "speech activity extends idle deadline within absolute lifetime");
}

var workerClock = new AdjustableTimeProvider(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
var workerDatabasePath = Path.Combine(databaseDirectory, "worker.sqlite");
var workerConfiguration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Backend:DatabasePath"] = workerDatabasePath,
        ["Backend:RetentionSweepSeconds"] = "1"
    })
    .Build();
using (var workerJournal = new OperationJournal(workerConfiguration, workerClock))
{
    var workerStore = new ConversationStore(workerClock, workerJournal);
    var workerSession = workerStore.Create(
        "device-a", Guid.NewGuid(), body, request with { Consent = new ConversationConsent(true, false) });
    Require(workerSession.Response is not null, "worker fixture session");
    var workerTurnId = Guid.NewGuid();
    var workerTurn = workerStore.SubmitTurn(
        "device-a", workerSession.Response!.ConversationId, Guid.NewGuid(), turnBody,
        turn with { ClientTurnId = workerTurnId });
    Require(workerTurn.Status == 202 && workerStore.CachedOperationCount == 1,
        "worker fixture cached result");
    Require(workerStore.SubmitSpeech(
        "device-a", workerSession.Response.ConversationId, Guid.NewGuid(), speechBody,
        new SpeechRequest(workerTurnId, "device")).Status == 202
        && workerStore.CachedAudioCount == 1,
        "worker fixture cached audio");
    workerClock.Advance(TimeSpan.FromHours(24));

    using var worker = new RetentionWorker(
        workerClock,
        new DeviceAccessStore(workerConfiguration, workerClock),
        workerStore,
        workerJournal,
        workerConfiguration,
        NullLogger<RetentionWorker>.Instance);
    await worker.StartAsync(CancellationToken.None);
    for (var attempt = 0; attempt < 30 && workerStore.CachedOperationCount != 0; attempt++)
    {
        await Task.Delay(100);
    }
    Require(workerStore.CachedOperationCount == 0, "hosted worker clears RAM without traffic");
    Require(workerStore.CachedAudioCount == 0, "hosted worker clears audio without traffic");
    await worker.StopAsync(CancellationToken.None);

    using var workerConnection = new SqliteConnection($"Data Source={workerDatabasePath}");
    workerConnection.Open();
    using var count = workerConnection.CreateCommand();
    count.CommandText = "SELECT COUNT(*) FROM turn_operations";
    Require((long)count.ExecuteScalar()! == 0, "hosted worker clears SQLite without traffic");
}

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

Console.WriteLine("Backend clock tests: expiry, restart, fake voice, cost accounting and migrations passed");
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

    foreach (var table in new[]
    {
        "turn_operations",
        "turn_idempotency",
        "conversation_turn_usage",
        "conversation_creates",
        "mutation_idempotency",
        "cost_reservations",
        "cost_session_usage",
        "cost_period_usage"
    })
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
