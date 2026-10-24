using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Show;

// The document, replay keys, grants and completion outbox share the existing backend
// SQLite database. An immediate transaction fences concurrent workers/processes.
internal sealed partial class DuelStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string connectionString;
    private readonly TimeProvider clock;
    private readonly IDuelIdentitySource identities;
    private readonly IDuelShowSlot slot;
    private readonly IDataProtector protector;
    private readonly object gate = new();
    private readonly bool commentaryEnabled;

    public DuelStore(IConfiguration configuration, TimeProvider clock, IDuelIdentitySource identities,
        IDuelShowSlot slot, IDataProtectionProvider protection)
    {
        this.clock = clock;
        this.identities = identities;
        this.slot = slot;
        commentaryEnabled = configuration.GetValue<bool>("Show:DuelCommentaryEnabled");
        protector = protection.CreateProtector("DildoVander.DuelGrantReplay.v1");
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(configuration["Backend:DatabasePath"]
                ?? throw new InvalidOperationException("Backend:DatabasePath is required.")),
            DefaultTimeout = 5
        }.ToString();
        using var connection = Open();
        Execute(connection, null, """
            CREATE TABLE IF NOT EXISTS show_duels (
                id TEXT PRIMARY KEY, device_id TEXT NOT NULL, evening_id TEXT NOT NULL,
                revision INTEGER NOT NULL, state TEXT NOT NULL, document TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS show_duel_evening ON show_duels(device_id, evening_id);
            CREATE TABLE IF NOT EXISTS show_duel_operations (
                scope TEXT NOT NULL, operation_key TEXT NOT NULL, body_hash TEXT NOT NULL,
                duel_id TEXT NOT NULL, protected_token TEXT, PRIMARY KEY(scope,operation_key));
            CREATE TABLE IF NOT EXISTS show_duel_grants (
                secret_hash TEXT PRIMARY KEY, duel_id TEXT NOT NULL, side TEXT NOT NULL,
                expires INTEGER NOT NULL, revoked INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS show_duel_events (
                duel_id TEXT PRIMARY KEY, document TEXT NOT NULL, delivered INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS show_duel_commentary (
                duel_id TEXT PRIMARY KEY, state TEXT NOT NULL, result TEXT, audit TEXT);
            CREATE TABLE IF NOT EXISTS show_duel_identity_tombstones (
                identity_id TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS show_duel_deleted_releases (
                duel_id TEXT PRIMARY KEY, device_id TEXT NOT NULL);
            """);
    }

    public DuelView Create(string deviceId, DuelCreateRequest request)
    {
        ValidateKey(request.OperationKey);
        if (request.Mode is not ("autoplay" or "interactive" or "exhibition")
            || !request.ConsentConfirmed || request.LeftIdentityId == request.RightIdentityId)
        {
            throw new DuelException(400, "invalid_duel");
        }
        lock (gate)
        {
            // Other feature owners have their own locks and SQLite writers. Never
            // call them while holding a duel write transaction (lock inversion).
            RequireEvening(deviceId, request.EveningId);
            var left = identities.Read(request.LeftIdentityId, request.EveningId, deviceId)
                ?? throw new DuelException(409, "identity_unavailable");
            var right = identities.Read(request.RightIdentityId, request.EveningId, deviceId)
                ?? throw new DuelException(409, "identity_unavailable");
            if ((left.WandName is null) != (right.WandName is null))
                throw new DuelException(409, "incompatible_wand_models");
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using (var revoked = Command(connection, transaction,
                "SELECT COUNT(*) FROM show_duel_identity_tombstones WHERE identity_id=$left OR identity_id=$right",
                ("$left", left.IdentityId), ("$right", right.IdentityId)))
            {
                if (Convert.ToInt32(revoked.ExecuteScalar()) != 0) { throw new DuelException(409, "identity_unavailable"); }
            }
            var replay = Replay(connection, transaction, $"{deviceId}:create", request.OperationKey, request);
            if (replay is not null) { return Load(connection, transaction, replay.Value.Id).View(); }
            var seed = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var rulesVersion = left.WandName is not null && right.WandName is not null
                ? DuelRules.CharacterVersion : DuelRules.Version;
            var duel = new DuelDocument
            {
                Id = Guid.NewGuid().ToString(), DeviceId = deviceId, EveningId = request.EveningId,
                Mode = request.Mode, Seed = seed, Left = left, Right = right,
                RulesVersion = rulesVersion,
                RequiresParticipantConsent = DuelRules.IsPersonalized(rulesVersion) && request.Mode == "interactive",
                State = request.Mode == "interactive" ? "AwaitingParticipants" : "Ready",
                Commitments = Enumerable.Range(1, 3).Select(round => DuelRules.Commit(seed, round, rulesVersion)).ToArray()
            };
            Execute(connection, transaction,
                "INSERT INTO show_duels VALUES($id,$device,$evening,$revision,$state,$document)",
                ("$id", duel.Id), ("$device", deviceId), ("$evening", duel.EveningId),
                ("$revision", duel.Revision), ("$state", duel.State), ("$document", Serialize(duel)));
            Remember(connection, transaction, $"{deviceId}:create", request.OperationKey, request, duel.Id);
            transaction.Commit();
            return duel.View();
        }
    }

    public DuelView Read(string id, string? deviceId = null, bool publicView = false)
    {
        using var connection = Open();
        var duel = Load(connection, null, id);
        if (deviceId is not null && duel.DeviceId != deviceId) { throw new DuelException(404, "duel_unavailable"); }
        if (publicView) { RequireVisible(duel); }
        return duel.View() with { Commentary = ReadCommentary(connection, duel.Id) };
    }

    public IReadOnlyList<DuelView> List(string deviceId, string eveningId)
    {
        using var connection = Open();
        using var command = Command(connection, null,
            "SELECT document FROM show_duels WHERE device_id=$device AND evening_id=$evening ORDER BY rowid DESC LIMIT 40",
            ("$device", deviceId), ("$evening", eveningId));
        using var reader = command.ExecuteReader();
        var result = new List<DuelView>();
        while (reader.Read()) { result.Add(Deserialize<DuelDocument>(reader.GetString(0)).View()); }
        return result;
    }

    public DuelView Start(string deviceId, string id, DuelCommand request)
    {
        ValidateKey(request.OperationKey);
        lock (gate)
        {
            var current = Read(id, deviceId);
            if (current.State is "Running" or "Completed")
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                _ = Replay(connection, transaction, $"{id}:start", request.OperationKey, request);
                return current;
            }
            if (current.State != "Ready" || current.Revision != request.Revision)
            {
                throw new DuelException(409, "duel_not_ready");
            }
            if (!DuelRules.Supports(current.RulesVersion)) { throw new DuelException(409, "rules_version_unavailable"); }
            RequireEvening(deviceId, current.EveningId);
            _ = Read(id, deviceId, publicView: true);
            var narration = slot.BindNarration(deviceId);
            // The show coordinator persists occupancy in this same database. Acquire
            // before opening our write transaction, and release only after rollback.
            if (!slot.TryOccupy(deviceId, current.EveningId, id)) { throw new DuelException(409, "show_unavailable"); }
            try
            {
                return Mutate(deviceId, id, "start", request.OperationKey, request, duel =>
                {
                    Revision(duel, request.Revision);
                    if (duel.State != "Ready") { throw new DuelException(409, "duel_not_ready"); }
                    duel.State = "Running";
                    duel.NarrationMode = narration.Mode;
                    duel.NarrationUnavailableReason = narration.UnavailableReason;
                    duel.NarrationStage = narration.Mode == "physical_device" ? "intro_dispatch" : "choice";
                    duel.ChoiceRevision = duel.Revision + 1;
                    duel.Deadline = narration.Mode == "physical_device" ? null
                        : clock.GetUtcNow().AddSeconds(duel.Mode == "interactive" ? 15 : 3);
                }, before: (connection, transaction, duel) =>
                {
                    if (duel.RequiresParticipantConsent && (!duel.LeftJoined || !duel.RightJoined
                        || ActiveGrantCount(connection, transaction, id) != 2))
                    { throw new DuelException(409, "participants_not_joined"); }
                });
            }
            catch
            {
                if (Read(id, deviceId).State != "Running") { slot.Release(deviceId, id); }
                throw;
            }
        }
    }

    public DuelView Cancel(string deviceId, string id, DuelCommand request)
    {
        var result = Mutate(deviceId, id, "cancel", request.OperationKey, request, duel =>
        {
            if (duel.State is "Cancelled" or "Interrupted" or "Completed") { return; }
            Revision(duel, request.Revision);
            duel.State = "Cancelled";
            duel.NarrationStage = "cancelled";
            duel.NarrationExpiresAt = null;
            duel.Deadline = null;
            duel.LeftChoice = duel.RightChoice = null;
            duel.Result = null;
        });
        if (result.State != "Running") { slot.Release(deviceId, id); }
        return result;
    }

    public DuelGrant Grant(string deviceId, string id, DuelGrantRequest request)
    {
        ValidateSide(request.Side);
        ValidateKey(request.OperationKey);
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var duel = LoadForDevice(connection, transaction, id, deviceId);
            var replay = Replay(connection, transaction, $"{id}:grant", request.OperationKey, request);
            if (replay is not null)
            {
                var token = protector.Unprotect(replay.Value.Token!);
                var expiry = GrantExpiry(connection, transaction, id, request.Side, token);
                return new(token, request.Side, expiry, duel.View());
            }
            Revision(duel, request.Revision);
            if (duel.Mode != "interactive" || duel.State is not ("AwaitingParticipants" or "Ready" or "Running"))
            {
                throw new DuelException(409, "duel_not_interactive");
            }
            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var expires = clock.GetUtcNow().AddMinutes(30);
            Execute(connection, transaction, "UPDATE show_duel_grants SET revoked=1 WHERE duel_id=$id AND side=$side",
                ("$id", id), ("$side", request.Side));
            Execute(connection, transaction, "INSERT INTO show_duel_grants(secret_hash,duel_id,side,expires) VALUES($hash,$id,$side,$expires)",
                ("$hash", Hash(secret)), ("$id", id), ("$side", request.Side), ("$expires", expires.ToUnixTimeSeconds()));
            if (request.Side == "left") { duel.LeftJoined = false; } else { duel.RightJoined = false; }
            if (duel.RequiresParticipantConsent && duel.State == "Ready") { duel.State = "AwaitingParticipants"; }
            if (!duel.RequiresParticipantConsent && duel.State == "AwaitingParticipants"
                && ActiveGrantCount(connection, transaction, id) == 2) { duel.State = "Ready"; }
            Save(connection, transaction, duel);
            Remember(connection, transaction, $"{id}:grant", request.OperationKey, request, id, protector.Protect(secret));
            transaction.Commit();
            return new(secret, request.Side, expires, duel.View());
        }
    }

    public DuelView Revoke(string deviceId, string id, DuelGrantRequest request)
    {
        ValidateSide(request.Side);
        return Mutate(deviceId, id, "revoke", request.OperationKey, request, duel =>
        {
            Revision(duel, request.Revision);
        }, (connection, transaction, duel) =>
        {
            Execute(connection, transaction, "UPDATE show_duel_grants SET revoked=1 WHERE duel_id=$id AND side=$side",
                ("$id", id), ("$side", request.Side));
            if (request.Side == "left") { duel.LeftJoined = false; } else { duel.RightJoined = false; }
            if (duel.State == "Ready" && duel.Mode == "interactive") { duel.State = "AwaitingParticipants"; }
        });
    }

    public DuelView Join(string id, string token, DuelJoinRequest request)
    {
        ValidateSide(request.Side);
        if (!request.ConsentConfirmed) { throw new DuelException(400, "participant_consent_required"); }
        var current = Read(id, publicView: true);
        RequireEvening(current.DeviceId, current.EveningId);
        // Include the grant generation in the replay scope. Reissuing a side's
        // invitation requires fresh consent, even for an identical request key.
        return Mutate(null, id, "join:" + Hash(token), request.OperationKey, request, duel =>
        {
            if (duel.Mode != "interactive" || duel.State is not ("AwaitingParticipants" or "Ready" or "Running"))
            { throw new DuelException(409, "duel_not_interactive"); }
            if (request.Side == "left") { duel.LeftJoined = true; } else { duel.RightJoined = true; }
        }, (connection, transaction, duel) =>
        {
            if (duel.State == "AwaitingParticipants" && duel.LeftJoined && duel.RightJoined
                && ActiveGrantCount(connection, transaction, id) == 2) { duel.State = "Ready"; }
        }, before: (connection, transaction, duel) => GrantExpiry(connection, transaction, id, request.Side, token));
    }

    public DuelView Choose(string id, string token, DuelChoiceRequest request)
    {
        ValidateSide(request.Side);
        if (!DuelRules.Actions.Contains(request.Action)) { throw new DuelException(400, "invalid_action"); }
        var current = Read(id, publicView: true);
        RequireEvening(current.DeviceId, current.EveningId);
        return Mutate(null, id, "choice", request.OperationKey, request, duel =>
        {
            if (duel.Mode != "interactive" || duel.State != "Running" || duel.NarrationStage != "choice"
                || duel.Deadline is null || request.Round != duel.Rounds.Count + 1
                || clock.GetUtcNow() >= duel.Deadline || !slot.IsCurrent(duel.DeviceId, id))
            {
                throw new DuelException(409, "round_closed");
            }
            var existing = request.Side == "left" ? duel.LeftChoice : duel.RightChoice;
            if (existing is not null)
            {
                if (existing != request.Action) { throw new DuelException(409, "choice_locked"); }
                return;
            }
            if (duel.RequiresParticipantConsent && !(request.Side == "left" ? duel.LeftJoined : duel.RightJoined))
            { throw new DuelException(403, "participant_consent_required"); }
            // A second phone may submit from the same round snapshot as the
            // first. Its action is independent; only the round's opening revision
            // matters. Older rounds and future revisions remain rejected.
            if (DuelRules.IsPersonalized(duel.RulesVersion) && duel.ChoiceRevision is { } floor)
            {
                if (request.Revision < floor || request.Revision > duel.Revision)
                { throw new DuelException(409, "stale_revision"); }
            }
            else { Revision(duel, request.Revision); }
            if (request.Side == "left") { duel.LeftChoice = request.Action; }
            else { duel.RightChoice = request.Action; }
        }, before: (connection, transaction, duel) => GrantExpiry(connection, transaction, id, request.Side, token));
    }

    public void Tick()
    {
        ReleaseDeletedDuels();
        var released = new List<(string Device, string Id)>();
        var beats = new List<(string Device, string Id, int Round, string Quip)>();
        var narration = new List<DuelNarrationDispatch>();
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null, "SELECT document FROM show_duels WHERE state='Running' LIMIT 100");
            var running = new List<DuelDocument>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read()) { running.Add(Deserialize<DuelDocument>(reader.GetString(0))); }
            }
            var activeEvenings = running.Select(duel => (duel.DeviceId, duel.EveningId)).Distinct()
                .ToDictionary(scope => scope, scope => identities.IsOpenEvening(scope.DeviceId, scope.EveningId));
            using var transaction = connection.BeginTransaction();
            foreach (var duel in running)
            {
                if (!DuelRules.Supports(duel.RulesVersion) || !slot.IsCurrent(duel.DeviceId, duel.Id)
                    || !activeEvenings[(duel.DeviceId, duel.EveningId)])
                {
                    Interrupt(duel);
                    Save(connection, transaction, duel);
                    released.Add((duel.DeviceId, duel.Id));
                    continue;
                }
                if (ProcessNarrationState(connection, transaction, duel, narration, released)) { continue; }
                if (duel.Deadline > clock.GetUtcNow() && (duel.LeftChoice is null || duel.RightChoice is null)) { continue; }
                duel.Rounds.Add(DuelRules.Resolve(duel.Seed, duel.Rounds.Count + 1, duel.Left, duel.Right,
                    duel.Commitments[duel.Rounds.Count], duel.LeftChoice, duel.RightChoice, duel.RulesVersion));
                beats.Add((duel.DeviceId, duel.Id, duel.Rounds.Count, duel.Rounds[^1].Quip));
                duel.LeftChoice = duel.RightChoice = null;
                if (duel.Rounds.Count == 3)
                {
                    duel.Result = DuelRules.Result(duel.Rounds, duel.Mode == "exhibition");
                }
                if (duel.NarrationMode == "physical_device")
                {
                    duel.NarrationStage = "round_dispatch";
                    duel.Deadline = null;
                    Save(connection, transaction, duel);
                    narration.Add(CaptureNarration(duel));
                }
                else if (duel.Rounds.Count == 3)
                {
                    CompleteDuel(connection, transaction, duel);
                    released.Add((duel.DeviceId, duel.Id));
                }
                else
                {
                    duel.ChoiceRevision = duel.Revision + 1;
                    duel.Deadline = clock.GetUtcNow().AddSeconds(duel.Mode == "interactive" ? 15 : 3);
                    Save(connection, transaction, duel);
                }
            }
            transaction.Commit();
        }
        foreach (var beat in beats) { slot.PublishRound(beat.Device, beat.Id, beat.Round, beat.Quip); }
        foreach (var dispatch in narration) { DispatchNarration(dispatch); }
        foreach (var (device, id) in released) { slot.Release(device, id); }
    }

    public IReadOnlyList<DuelCompletionEvent> PendingEvents()
    {
        using var connection = Open();
        using var command = Command(connection, null, "SELECT document FROM show_duel_events WHERE delivered=0 LIMIT 100");
        using var reader = command.ExecuteReader();
        var events = new List<DuelCompletionEvent>();
        while (reader.Read()) { events.Add(Deserialize<DuelCompletionEvent>(reader.GetString(0))); }
        return events;
    }

    public void AcknowledgeEvent(string id)
    {
        using var connection = Open();
        Execute(connection, null, "UPDATE show_duel_events SET delivered=1 WHERE duel_id=$id", ("$id", id));
    }

    // Called by keepsake deletion for every withdrawn immutable wizard identity.
    // The tombstone fences a Create that captured its source before deletion.
    public void PurgeIdentity(string identityId)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            PurgeIdentity(connection, transaction, identityId);
            transaction.Commit();
        }
        ReleaseDeletedDuels();
    }

    internal static void PurgeIdentity(SqliteConnection connection, SqliteTransaction transaction, string identityId)
    {
        // May run before the optional duel feature was instantiated in a legacy
        // host. Keep the revocation fence even when no duel tables exist yet.
        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS show_duel_identity_tombstones (identity_id TEXT PRIMARY KEY)");
        Execute(connection, transaction, "INSERT OR IGNORE INTO show_duel_identity_tombstones VALUES($identity)", ("$identity", identityId));
        using (var exists = Command(connection, transaction, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='show_duels'"))
        {
            if (Convert.ToInt32(exists.ExecuteScalar()) == 0) { return; }
        }
        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS show_duel_deleted_releases (duel_id TEXT PRIMARY KEY, device_id TEXT NOT NULL)");
        var released = new List<(string Device, string Id)>();
        using (var command = Command(connection, transaction,
            "SELECT device_id,id FROM show_duels WHERE json_extract(document,'$.left.identityId')=$identity OR json_extract(document,'$.right.identityId')=$identity",
            ("$identity", identityId)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) { released.Add((reader.GetString(0), reader.GetString(1))); }
        }
        foreach (var (device, id) in released)
        {
            Execute(connection, transaction, "INSERT OR IGNORE INTO show_duel_deleted_releases VALUES($id,$device)", ("$id", id), ("$device", device));
            Execute(connection, transaction, "DELETE FROM show_duel_grants WHERE duel_id=$id", ("$id", id));
            Execute(connection, transaction, "DELETE FROM show_duel_operations WHERE duel_id=$id", ("$id", id));
            Execute(connection, transaction, "DELETE FROM show_duel_events WHERE duel_id=$id", ("$id", id));
            Execute(connection, transaction, "DELETE FROM show_duel_commentary WHERE duel_id=$id", ("$id", id));
            Execute(connection, transaction, "DELETE FROM show_duels WHERE id=$id", ("$id", id));
        }
    }

    private void ReleaseDeletedDuels()
    {
        var released = new List<(string Device, string Id)>();
        using (var connection = Open())
        using (var command = Command(connection, null, "SELECT device_id,duel_id FROM show_duel_deleted_releases LIMIT 100"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) { released.Add((reader.GetString(0), reader.GetString(1))); }
        }
        foreach (var (device, id) in released)
        {
            slot.Release(device, id);
            using var connection = Open();
            Execute(connection, null, "DELETE FROM show_duel_deleted_releases WHERE duel_id=$id", ("$id", id));
        }
    }

    private DuelView Mutate(string? deviceId, string id, string operation, string key, object body,
        Action<DuelDocument> change, Action<SqliteConnection, SqliteTransaction, DuelDocument>? after = null,
        Action<SqliteConnection, SqliteTransaction, DuelDocument>? before = null)
    {
        ValidateKey(key);
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var duel = deviceId is null ? Load(connection, transaction, id) : LoadForDevice(connection, transaction, id, deviceId);
            before?.Invoke(connection, transaction, duel);
            if (Replay(connection, transaction, $"{id}:{operation}", key, body) is not null) { return duel.View(); }
            var previous = Serialize(duel);
            change(duel);
            after?.Invoke(connection, transaction, duel);
            if (after is not null || Serialize(duel) != previous) { Save(connection, transaction, duel); }
            Remember(connection, transaction, $"{id}:{operation}", key, body, id);
            transaction.Commit();
            return duel.View();
        }
    }

    private void RequireVisible(DuelDocument duel)
    {
        if (!identities.IsActiveEvening(duel.DeviceId, duel.EveningId)
            || identities.Read(duel.Left.IdentityId, duel.EveningId, duel.DeviceId) is null
            || identities.Read(duel.Right.IdentityId, duel.EveningId, duel.DeviceId) is null)
        {
            throw new DuelException(404, "duel_unavailable");
        }
    }
    private void RequireEvening(string deviceId, string eveningId)
    {
        if (!identities.IsOpenEvening(deviceId, eveningId)) { throw new DuelException(409, "evening_unavailable"); }
    }
    private static void ValidateSide(string side)
    {
        if (side is not ("left" or "right")) { throw new DuelException(400, "invalid_side"); }
    }
    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 120) { throw new DuelException(400, "invalid_operation_key"); }
    }
    private static void Revision(DuelDocument duel, int revision)
    {
        if (duel.Revision != revision) { throw new DuelException(409, "stale_revision"); }
    }
    private DateTimeOffset GrantExpiry(SqliteConnection connection, SqliteTransaction transaction, string id, string side, string token)
    {
        if (token.Length is < 40 or > 128) { throw new DuelException(403, "grant_unavailable"); }
        using var command = Command(connection, transaction,
            "SELECT expires FROM show_duel_grants WHERE secret_hash=$hash AND duel_id=$id AND side=$side AND revoked=0 AND expires>$now",
            ("$hash", Hash(token)), ("$id", id), ("$side", side), ("$now", clock.GetUtcNow().ToUnixTimeSeconds()));
        return command.ExecuteScalar() is long expires ? DateTimeOffset.FromUnixTimeSeconds(expires)
            : throw new DuelException(403, "grant_unavailable");
    }
    private int ActiveGrantCount(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = Command(connection, transaction,
            "SELECT COUNT(DISTINCT side) FROM show_duel_grants WHERE duel_id=$id AND revoked=0 AND expires>$now",
            ("$id", id), ("$now", clock.GetUtcNow().ToUnixTimeSeconds()));
        return Convert.ToInt32(command.ExecuteScalar());
    }
    private static DuelDocument LoadForDevice(SqliteConnection connection, SqliteTransaction transaction, string id, string device)
    {
        var duel = Load(connection, transaction, id);
        return duel.DeviceId == device ? duel : throw new DuelException(404, "duel_unavailable");
    }
    private static DuelDocument Load(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        using var command = Command(connection, transaction, "SELECT document FROM show_duels WHERE id=$id", ("$id", id));
        return command.ExecuteScalar() is string json ? Deserialize<DuelDocument>(json)
            : throw new DuelException(404, "duel_unavailable");
    }
    private static void Save(SqliteConnection connection, SqliteTransaction transaction, DuelDocument duel)
    {
        var previous = duel.Revision++;
        var changed = Execute(connection, transaction,
            "UPDATE show_duels SET revision=$revision,state=$state,document=$document WHERE id=$id AND revision=$previous",
            ("$revision", duel.Revision), ("$state", duel.State), ("$document", Serialize(duel)),
            ("$id", duel.Id), ("$previous", previous));
        if (changed != 1) { throw new DuelException(409, "stale_revision"); }
    }
    private static (string Id, string? Token)? Replay(SqliteConnection connection, SqliteTransaction transaction,
        string scope, string key, object body)
    {
        using var command = Command(connection, transaction,
            "SELECT body_hash,duel_id,protected_token FROM show_duel_operations WHERE scope=$scope AND operation_key=$key",
            ("$scope", scope), ("$key", key));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) { return null; }
        if (reader.GetString(0) != Hash(Serialize(body))) { throw new DuelException(409, "idempotency_conflict"); }
        return (reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }
    private static void Remember(SqliteConnection connection, SqliteTransaction transaction,
        string scope, string key, object body, string id, string? token = null) => Execute(connection, transaction,
        "INSERT INTO show_duel_operations VALUES($scope,$key,$hash,$id,$token)",
        ("$scope", scope), ("$key", key), ("$hash", Hash(Serialize(body))), ("$id", id), ("$token", token));
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Key, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (key, value) in parameters) { command.Parameters.AddWithValue(key, value ?? DBNull.Value); }
        return command;
    }
    private static int Execute(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Key, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Serialize(object value) => JsonSerializer.Serialize(value, Json);
    private static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)
        ?? throw new InvalidOperationException("Invalid stored duel.");
}
