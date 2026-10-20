using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.DataProtection;

namespace Kufr.Backend.Features.Keepsakes;

internal sealed partial class KeepsakeStore
{
    public KeepsakeSession Bootstrap()
    {
        var session = new KeepsakeSession(Secret(), Secret());
        lock (gate)
        {
            using var connection = Open();
            Execute(connection, null, "INSERT INTO dv10_sessions(secret_hash,csrf_hash,csrf_protected,expires) VALUES($s,$c,$p,$e)",
                ("$s", Hash(session.Token)), ("$c", Hash(session.CsrfToken)),
                ("$p", replayProtector.Protect(session.CsrfToken)), ("$e", Now.AddDays(30).ToUnixTimeSeconds()));
        }
        return session;
    }

    public KeepsakeSession? ResumeSession(string token)
    {
        if (!ValidSecret(token)) { return null; }
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null,
                "SELECT csrf_protected FROM dv10_sessions WHERE secret_hash=$s AND expires>$e",
                ("$s", Hash(token)), ("$e", Now.ToUnixTimeSeconds()));
            var protectedCsrf = command.ExecuteScalar() as string;
            return protectedCsrf is null ? null : new(token, replayProtector.Unprotect(protectedCsrf));
        }
    }

    public bool ValidateSession(string token, string? csrfToken = null)
    {
        if (!ValidSecret(token) || (csrfToken is not null && !ValidSecret(csrfToken))) { return false; }
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null,
                "SELECT csrf_hash FROM dv10_sessions WHERE secret_hash=$s AND expires>$e",
                ("$s", Hash(token)), ("$e", Now.ToUnixTimeSeconds()));
            var csrfHash = command.ExecuteScalar() as string;
            return csrfHash is not null && (csrfToken is null || csrfHash == Hash(csrfToken));
        }
    }

    public void RevokeSession(string token)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, "DELETE FROM dv10_sessions WHERE secret_hash=$s", ("$s", Hash(token)));
            Execute(connection, transaction, "DELETE FROM dv10_grants WHERE session_hash=$s", ("$s", Hash(token)));
            RecordRevocation(connection, transaction, null, Hash(token), "session_revoked");
            transaction.Commit();
        }
    }

    public KeepsakeLink IssueClaim(string id, string deviceId, string? idempotencyKey = null)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var document = Load(connection, transaction, id);
            if (document.Snapshot.DeviceId != deviceId) { throw new KeepsakeException("keepsake_not_found", 404); }
            var scope = "issue-claim:" + id + ":" + Hash(deviceId);
            if (idempotencyKey is not null)
            {
                var replay = Replay(connection, transaction, scope, idempotencyKey, id);
                if (replay is not null)
                {
                    var parts = replayProtector.Unprotect(replay).Split('|');
                    return new(parts[0], DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[1])), id);
                }
            }
            using (var owner = Command(connection, transaction,
                "SELECT 1 FROM dv10_keepsakes WHERE id=$id AND claimed=1", ("$id", id)))
            {
                if (owner.ExecuteScalar() is not null) { throw new KeepsakeException("operator_restore_required", 409); }
            }
            Execute(connection, transaction, "DELETE FROM dv10_links WHERE keepsake_id=$id AND kind='claim'", ("$id", id));
            RecordRevocation(connection, transaction, id, null, "claim_reissued");
            var link = MakeLink(connection, transaction, id, "claim", null, TimeSpan.FromMinutes(10));
            if (idempotencyKey is not null)
            {
                Remember(connection, transaction, scope, idempotencyKey, id,
                    replayProtector.Protect(link.Token + "|" + link.ExpiresAt.ToUnixTimeSeconds()));
            }
            transaction.Commit();
            return link;
        }
    }

    // This authority-changing operation is reachable only from the confirmed operator
    // endpoint. It retains the same keepsake and all content, revoking prior owners.
    public KeepsakeLink RestoreOwnerClaim(string id, string idempotencyKey)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Load(connection, transaction, id);
            var scope = "restore-owner:" + id;
            var replay = Replay(connection, transaction, scope, idempotencyKey, id);
            if (replay is not null)
            {
                var parts = replayProtector.Unprotect(replay).Split('|');
                return new(parts[0], DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[1])), id);
            }
            Execute(connection, transaction, "DELETE FROM dv10_grants WHERE keepsake_id=$id", ("$id", id));
            Execute(connection, transaction, "DELETE FROM dv10_links WHERE keepsake_id=$id", ("$id", id));
            RecordRevocation(connection, transaction, id, null, "operator_owner_restored");
            var link = MakeLink(connection, transaction, id, "claim", null, TimeSpan.FromMinutes(10));
            Remember(connection, transaction, scope, idempotencyKey, id,
                replayProtector.Protect(link.Token + "|" + link.ExpiresAt.ToUnixTimeSeconds()));
            transaction.Commit();
            return link;
        }
    }

    private KeepsakeLink MakeLink(SqliteConnection connection, SqliteTransaction transaction,
        string id, string kind, string? edition, TimeSpan duration)
    {
        var link = new KeepsakeLink(Secret(), kind == "recovery" ? DateTimeOffset.MaxValue : Now.Add(duration), id, edition);
        Execute(connection, transaction,
            "INSERT INTO dv10_links(secret_hash,keepsake_id,kind,edition_id,expires) VALUES($s,$id,$k,$ed,$e)",
            ("$s", Hash(link.Token)), ("$id", id), ("$k", kind), ("$ed", edition), ("$e", link.ExpiresAt.ToUnixTimeSeconds()));
        return link;
    }

    public string Claim(string token, string session, string idempotencyKey) => Attach(token, session, idempotencyKey, "claim");
    public string Recover(string token, string session, string idempotencyKey) => Attach(token, session, idempotencyKey, "recovery");
    public string ShareClaim(string token, string session, string idempotencyKey) => Attach(token, session, idempotencyKey, "share");

    private string Attach(string token, string session, string key, string kind)
    {
        if (!ValidSecret(token)) { throw new KeepsakeException("invalid_link", 404); }
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            RequireSession(connection, transaction, session);
            var sessionHash = Hash(session);
            var scope = kind + ":" + sessionHash;
            var replay = Replay(connection, transaction, scope, key, token);
            if (replay is not null)
            {
                if (kind == "share") { ResolveShareEdition(token, session); }
                else { RequireAccess(connection, transaction, replay, session, KeepsakeScope.Read); }
                return replay;
            }
            using var command = Command(connection, transaction,
                "SELECT keepsake_id,edition_id,consumed_session FROM dv10_links WHERE secret_hash=$s AND kind=$k AND expires>$e",
                ("$s", Hash(token)), ("$k", kind), ("$e", Now.ToUnixTimeSeconds()));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) { throw new KeepsakeException("invalid_link", 404); }
            var id = reader.GetString(0);
            var edition = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var consumedBy = reader.IsDBNull(2) ? null : reader.GetString(2);
            reader.Close();
            Load(connection, transaction, id);
            if (kind == "claim" && consumedBy is not null && consumedBy != sessionHash)
            {
                throw new KeepsakeException("claim_already_used", 409);
            }
            if (kind == "claim")
            {
                Execute(connection, transaction, "UPDATE dv10_links SET consumed_session=$c WHERE secret_hash=$s",
                    ("$c", sessionHash), ("$s", Hash(token)));
                Execute(connection, transaction, "UPDATE dv10_keepsakes SET claimed=1 WHERE id=$id", ("$id", id));
            }
            Execute(connection, transaction,
                "INSERT OR REPLACE INTO dv10_grants(session_hash,keepsake_id,kind,edition_id,link_hash) VALUES($s,$id,$k,$ed,$l)",
                ("$s", sessionHash), ("$id", id), ("$k", kind == "share" ? "share" : "owner"), ("$ed", edition), ("$l", Hash(token)));
            Remember(connection, transaction, scope, key, token, id);
            RecordRevocation(connection, transaction, id, sessionHash, kind + "_attached");
            transaction.Commit();
            return id;
        }
    }

    private void RequireSession(SqliteConnection connection, SqliteTransaction? transaction, string session)
    {
        if (!ValidSecret(session)) { throw new KeepsakeException("invalid_session", 401); }
        using var command = Command(connection, transaction,
            "SELECT 1 FROM dv10_sessions WHERE secret_hash=$s AND expires>$e",
            ("$s", Hash(session)), ("$e", Now.ToUnixTimeSeconds()));
        if (command.ExecuteScalar() is null) { throw new KeepsakeException("invalid_session", 401); }
    }

    private string? RequireAccess(SqliteConnection connection, SqliteTransaction? transaction,
        string id, string session, KeepsakeScope scope, bool shareOnly = false, string? shareEditionId = null)
    {
        RequireSession(connection, transaction, session);
        var kind = shareOnly ? "share" : "owner";
        if (shareOnly && (scope != KeepsakeScope.Read || shareEditionId is null)) { throw new KeepsakeException("keepsake_not_found", 404); }
        using var command = Command(connection, transaction,
            "SELECT g.edition_id FROM dv10_grants g JOIN dv10_keepsakes k ON k.id=g.keepsake_id "
            + "WHERE g.session_hash=$s AND g.keepsake_id=$id AND g.kind=$k AND k.deleted=0 "
            + "AND ($share=0 OR g.edition_id=$edition) "
            + "AND (g.kind='owner' OR EXISTS(SELECT 1 FROM dv10_links l WHERE l.keepsake_id=g.keepsake_id "
            + "AND l.kind='share' AND l.secret_hash=g.link_hash AND l.edition_id=g.edition_id AND l.expires>$e)) LIMIT 1",
            ("$s", Hash(session)), ("$id", id), ("$k", kind), ("$e", Now.ToUnixTimeSeconds()),
            ("$share", shareOnly ? 1 : 0), ("$edition", shareEditionId));
        var edition = command.ExecuteScalar() as string;
        if (edition is null) { throw new KeepsakeException("keepsake_not_found", 404); }
        return string.IsNullOrEmpty(edition) ? null : edition;
    }

    public bool Authorize(string id, string session, KeepsakeScope scope, bool shareOnly = false, string? shareEditionId = null)
    {
        lock (gate)
        {
            using var connection = Open();
            try { RequireAccess(connection, null, id, session, scope, shareOnly, shareEditionId); return true; }
            catch (KeepsakeException) { return false; }
        }
    }

    public string ResolveShareEdition(string token, string session)
    {
        lock (gate)
        {
            using var connection = Open();
            RequireSession(connection, null, session);
            using var command = Command(connection, null,
                "SELECT l.edition_id FROM dv10_links l JOIN dv10_grants g ON g.link_hash=l.secret_hash "
                + "JOIN dv10_keepsakes k ON k.id=l.keepsake_id WHERE l.secret_hash=$token AND l.kind='share' "
                + "AND l.expires>$e AND g.session_hash=$session AND g.kind='share' AND k.deleted=0",
                ("$token", Hash(token)), ("$e", Now.ToUnixTimeSeconds()), ("$session", Hash(session)));
            return command.ExecuteScalar() as string ?? throw new KeepsakeException("invalid_link", 404);
        }
    }

    public KeepsakeLink CreateRecovery(string id, string session, string idempotencyKey) =>
        CreateOwnerLink(id, session, null, "recovery", idempotencyKey);
    public KeepsakeLink CreateShare(string id, string session, string editionId, string idempotencyKey) =>
        CreateOwnerLink(id, session, editionId, "share", idempotencyKey);

    private KeepsakeLink CreateOwnerLink(string id, string session, string? edition, string kind, string key)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            RequireAccess(connection, transaction, id, session, KeepsakeScope.Write);
            var document = Load(connection, transaction, id);
            if (kind == "share" && !document.Editions.Any(item => item.Id == edition
                && document.Assets.Any(asset => asset.Id == item.ArtworkAssetId && asset.Kind == "base_image")))
            {
                throw new KeepsakeException("edition_not_found", 404);
            }
            var scope = kind + ":" + id + ":" + Hash(session);
            var body = edition ?? "";
            var replay = Replay(connection, transaction, scope, key, body);
            if (replay is not null)
            {
                var parts = replayProtector.Unprotect(replay).Split('|');
                return new(parts[0], DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[1])), id, edition);
            }
            if (kind == "recovery") { Execute(connection, transaction, "DELETE FROM dv10_links WHERE keepsake_id=$id AND kind='recovery'", ("$id", id)); }
            var link = MakeLink(connection, transaction, id, kind, edition,
                kind == "share" ? TimeSpan.FromDays(7) : TimeSpan.Zero);
            Remember(connection, transaction, scope, key, body,
                replayProtector.Protect(link.Token + "|" + link.ExpiresAt.ToUnixTimeSeconds()));
            RecordRevocation(connection, transaction, id, null, kind + "_issued");
            transaction.Commit();
            return link;
        }
    }

    public void RevokeRecovery(string id, string session) => RevokeLinks(id, session, "recovery");
    public void RevokeShares(string id, string session) => RevokeLinks(id, session, "share");
    private void RevokeLinks(string id, string session, string kind)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            RequireAccess(connection, transaction, id, session, KeepsakeScope.Write);
            Execute(connection, transaction, "DELETE FROM dv10_links WHERE keepsake_id=$id AND kind=$k", ("$id", id), ("$k", kind));
            if (kind == "share") { Execute(connection, transaction, "DELETE FROM dv10_grants WHERE keepsake_id=$id AND kind='share'", ("$id", id)); }
            RecordRevocation(connection, transaction, id, null, kind + "_revoked");
            transaction.Commit();
        }
    }

    public void RevokeAllAccess(string id, string session)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            RequireAccess(connection, transaction, id, session, KeepsakeScope.Write);
            Execute(connection, transaction, "DELETE FROM dv10_links WHERE keepsake_id=$id", ("$id", id));
            Execute(connection, transaction, "DELETE FROM dv10_grants WHERE keepsake_id=$id", ("$id", id));
            Execute(connection, transaction, "UPDATE dv10_keepsakes SET account_access_epoch=account_access_epoch+1 WHERE id=$id", ("$id", id));
            RecordRevocation(connection, transaction, id, null, "all_access_revoked");
            transaction.Commit();
        }
    }
}
