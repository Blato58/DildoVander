using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Keepsakes;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Show;

internal sealed record OfflineOutcome(string OperationId, string DeviceId, string BootId,
    int Generation, string LocalEveningId, string? EveningId, string SourceMode, int Compartment,
    string CatalogVersion, long StartedAtDeviceMs, long CompletedAtDeviceMs,
    string TimeConfidence, string Outcome,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OperationEpoch = null);
internal sealed record OfflineOutcomeRequest(int SchemaVersion, OfflineOutcome Outcome);
internal sealed record ImportedOfflineOutcome(int SchemaVersion, string OperationId,
    string EveningId, string Locator, bool Duplicate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OperationEpoch = null);

// Import records only. No path from this service to the device command transport.
internal sealed class OfflineOutcomeStore
{
    private readonly object gate = new();
    private readonly string connectionString;
    private readonly KeepsakeStore keepsakes;
    private readonly EveningMemoryStore evenings;

    public OfflineOutcomeStore(IConfiguration config, OperationJournal journal,
        KeepsakeStore keepsakes, EveningMemoryStore evenings)
    {
        _ = journal; // The existing journal owns process locking and startup recovery.
        this.keepsakes = keepsakes;
        this.evenings = evenings;
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(config["Backend:DatabasePath"]!), DefaultTimeout = 5
        }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS show_offline_outcomes (
                device_id TEXT NOT NULL, operation_id TEXT NOT NULL,
                local_evening_id TEXT NOT NULL, evening_id TEXT NOT NULL,
                payload_hash TEXT NOT NULL, locator TEXT,
                provenance TEXT NOT NULL, retention TEXT NOT NULL DEFAULT 'manual_only',
                PRIMARY KEY(device_id, operation_id));
            CREATE TABLE IF NOT EXISTS show_offline_scoped_outcomes (
                device_id TEXT NOT NULL, operation_epoch TEXT NOT NULL, operation_id TEXT NOT NULL,
                local_evening_id TEXT NOT NULL, evening_id TEXT NOT NULL,
                payload_hash TEXT NOT NULL, locator TEXT,
                provenance TEXT NOT NULL, retention TEXT NOT NULL DEFAULT 'manual_only',
                PRIMARY KEY(device_id, operation_epoch, operation_id));
            """;
        command.ExecuteNonQuery();
    }

    internal ImportedOfflineOutcome Import(string authenticatedDevice, OfflineOutcome record, string provenance)
    {
        Validate(authenticatedDevice, record);
        var epoch = OperationEpoch.Key(record.OperationEpoch);
        var scoped = !OperationEpoch.Same(record.OperationEpoch, null);
        var operationId = scoped ? Guid.Parse(record.OperationId).ToString("D") : record.OperationId;
        // Null and explicit zero share the unchanged legacy identity and hash.
        var canonical = record with { OperationId = operationId, OperationEpoch = scoped ? epoch : null };
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical)));
        var outcomeId = scoped ? MapScopedOutcome(record.DeviceId, epoch, operationId) : operationId;
        var table = scoped ? "show_offline_scoped_outcomes" : "show_offline_outcomes";
        var identityPredicate = "device_id=$device AND operation_id=$operation" +
            (scoped ? " AND operation_epoch=$epoch" : "");
        var eveningId = MapEvening(record.DeviceId, record.LocalEveningId);
        lock (gate)
        {
            using var connection = Open();
            using var read = connection.CreateCommand();
            read.CommandText = $"SELECT payload_hash,locator,provenance FROM {table} WHERE {identityPredicate}";
            AddIdentityParameters(read, record.DeviceId, operationId, scoped ? epoch : null);
            bool duplicate;
            string? existingLocator = null;
            using (var reader = read.ExecuteReader())
            {
                duplicate = reader.Read();
                if (duplicate && reader.GetString(0) != hash)
                {
                    throw new KeepsakeException("offline_outcome_conflict", 409);
                }
                if (duplicate)
                {
                    existingLocator = reader.IsDBNull(1) ? null : reader.GetString(1);
                    provenance = reader.GetString(2);
                }
            }

            // Commit the immutable import before deriving archive content. A crash
            // between these stages resumes the same outcome; Issue is idempotent.
            using var insert = connection.CreateCommand();
            insert.CommandText = scoped ? """
                INSERT OR IGNORE INTO show_offline_scoped_outcomes
                (device_id,operation_epoch,operation_id,local_evening_id,evening_id,payload_hash,provenance)
                VALUES($device,$epoch,$operation,$local,$evening,$hash,$provenance)
                """ : """
                INSERT OR IGNORE INTO show_offline_outcomes
                (device_id,operation_id,local_evening_id,evening_id,payload_hash,provenance)
                VALUES($device,$operation,$local,$evening,$hash,$provenance)
                """;
            AddIdentityParameters(insert, record.DeviceId, operationId, scoped ? epoch : null);
            insert.Parameters.AddWithValue("$local", record.LocalEveningId);
            insert.Parameters.AddWithValue("$evening", eveningId);
            insert.Parameters.AddWithValue("$hash", hash);
            insert.Parameters.AddWithValue("$provenance", provenance);
            insert.ExecuteNonQuery();
            evenings.EnsureImportedShowEvening(record.DeviceId, eveningId, "Offline představení");
            if (!evenings.RecordImportedOfflineOutcome(record.DeviceId, eveningId, outcomeId,
                record.Compartment, provenance))
            { throw new KeepsakeException("offline_memory_not_eligible", 409); }
            if (existingLocator is not null) { return new(1, record.OperationId, eveningId, existingLocator, true, record.OperationEpoch); }
            var wand = PartyWandCatalog.Available(63).Single(w => w.Compartment == record.Compartment);
            var issued = keepsakes.Issue(new KeepsakeIssueSnapshot(outcomeId, record.DeviceId,
                record.Compartment, wand.StableId, wand.DisplayName, wand.Description,
                "completed_by_timing", record.CatalogVersion, eveningId));
            using var update = connection.CreateCommand();
            update.CommandText = $"UPDATE {table} SET locator=$locator WHERE {identityPredicate}";
            update.Parameters.AddWithValue("$locator", issued.Id);
            AddIdentityParameters(update, record.DeviceId, operationId, scoped ? epoch : null);
            update.ExecuteNonQuery();
            return new(1, record.OperationId, eveningId, issued.Id, duplicate, record.OperationEpoch);
        }
    }

    private static void AddIdentityParameters(SqliteCommand command, string deviceId,
        string operationId, string? epoch)
    {
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$operation", operationId);
        if (epoch is not null) { command.Parameters.AddWithValue("$epoch", epoch); }
    }

    private static string MapScopedOutcome(string device, string epoch, string operationId)
    {
        // Both archive and memory require this same stable identity. Memory uses
        // UUID conversation keys, so domain-separated digest bytes become a UUID.
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            "show-offline-outcome-v1\n" + device + "\n" + epoch + "\n" + operationId));
        return new Guid(digest.AsSpan(0, 16)).ToString("D");
    }

    internal static string MapEvening(string device, string localEvening)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("show-offline-evening-v1\n" + device + "\n" + localEvening));
        return new Guid(digest.AsSpan(0, 16)).ToString("D");
    }

    private static void Validate(string device, OfflineOutcome record)
    {
        if (!OperationEpoch.IsValid(record.OperationEpoch) || record.DeviceId != device || string.IsNullOrWhiteSpace(device) || device.Length > 128
            || !Guid.TryParseExact(record.OperationId, "D", out _)
            || !Guid.TryParseExact(record.BootId, "D", out _)
            || !Guid.TryParseExact(record.LocalEveningId, "D", out _)
            || record.Generation < 1 || record.EveningId is not null
            || record.SourceMode is not ("offline_scripted" or "operator")
            || record.Compartment is < 1 or > 6 || record.CatalogVersion != PartyWandCatalog.Version
            || record.TimeConfidence != "boot_monotonic" || record.Outcome != "completed_by_timing"
            || record.StartedAtDeviceMs < 0 || record.CompletedAtDeviceMs <= record.StartedAtDeviceMs
            || record.CompletedAtDeviceMs - record.StartedAtDeviceMs > 600_000)
        {
            throw new KeepsakeException("offline_outcome_not_eligible", 409);
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}

internal static class OfflineOutcomeEndpoints
{
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web);

    internal static OfflineOutcomeRequest? ParseRequest(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) { throw new JsonException("Invalid offline outcome request."); }
        foreach (var property in body.EnumerateObject())
        {
            if (!property.Name.Equals("outcome", StringComparison.OrdinalIgnoreCase)) { continue; }
            if (property.Value.ValueKind != JsonValueKind.Object) { continue; }
            var seenEpoch = false;
            foreach (var field in property.Value.EnumerateObject())
            {
                if (!field.Name.Equals("operationEpoch", StringComparison.OrdinalIgnoreCase)) { continue; }
                if (field.Name != "operationEpoch" || seenEpoch || field.Value.ValueKind != JsonValueKind.String
                    || !OperationEpoch.IsValid(field.Value.GetString()))
                {
                    throw new JsonException("Invalid offline operation epoch.");
                }
                seenEpoch = true;
            }
        }
        return body.Deserialize<OfflineOutcomeRequest>(RequestJson);
    }

    internal static void MapOfflineOutcomeEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/show/offline-outcomes", (HttpContext context, DeviceAccessStore access,
            ServerAdminAccess admin, IConfiguration config, OfflineOutcomeStore store) =>
            ImportAsync(context, null, access, admin, config, store)).RequireRateLimiting("keepsake");
        app.MapPost("/v1/show/devices/{deviceId}/offline-outcomes", (string deviceId, HttpContext context,
            DeviceAccessStore access, ServerAdminAccess admin, IConfiguration config, OfflineOutcomeStore store) =>
            ImportAsync(context, deviceId, access, admin, config, store)).RequireRateLimiting("keepsake");
    }

    private static async Task<IResult> ImportAsync(HttpContext context, string? routeDevice,
        DeviceAccessStore access, ServerAdminAccess admin, IConfiguration config, OfflineOutcomeStore store)
    {
            if (!config.GetValue<bool>("Keepsake:Enabled")) { return Results.NotFound(); }
            var account = AccountShowAuthorization.IsAccount(context);
            if (account)
            {
                if (routeDevice is null) { return ApiProblem.Create(context, 403, "device_scope_required"); }
                var denied = await AccountShowAuthorization.RequireAsync(context, routeDevice);
                if (denied is not null) { return denied; }
            }
            var device = account ? null : DeviceAccessEndpoints.Authenticate(context, access);
            var isAdmin = account || admin.Authenticate(context);
            if (device is null && !isAdmin) { return Results.Unauthorized(); }
            try
            {
                var body = await context.Request.ReadFromJsonAsync<JsonElement>(RequestJson, context.RequestAborted);
                var request = ParseRequest(body);
                if (request is null || request.SchemaVersion != 1 || request.Outcome is null) { return Results.BadRequest(); }
                var target = request.Outcome.DeviceId;
                if (routeDevice is not null && routeDevice != target) { return Results.BadRequest(); }
                if (isAdmin && !config.GetSection("Devices").GetChildren().Any(d => d["DeviceId"] == target))
                { return Results.NotFound(); }
                if (!isAdmin && device != target) { return Results.Unauthorized(); }
                return Results.Json(store.Import(target, request.Outcome, isAdmin ? "operator_relay" : "device_report"));
            }
            catch (JsonException) { return Results.BadRequest(); }
            catch (KeepsakeException error) { return Results.Json(new { errorCode = error.Code }, statusCode: error.Status); }
    }
}
