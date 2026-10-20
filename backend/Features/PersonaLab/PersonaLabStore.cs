using System.Text.Json;
using Kufr.Backend.Features.GptLive;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.PersonaLab;

// A separate database is intentional: fixtures and test turns never enter guest memory.
internal sealed partial class PersonaLabStore
{
    private readonly string connectionString;
    private readonly string currentBaselineId;
    private readonly Lock gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal PersonaLabStore(string databasePath, LivePromptSnapshot baseline)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath), Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, DefaultTimeout = 10
        }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS lab_documents (
                kind TEXT NOT NULL, id TEXT NOT NULL, owner TEXT NOT NULL,
                version INTEGER NOT NULL, json TEXT NOT NULL, PRIMARY KEY(kind,id));
            CREATE INDEX IF NOT EXISTS lab_owner ON lab_documents(kind,owner);
            CREATE TABLE IF NOT EXISTS lab_state (id INTEGER PRIMARY KEY CHECK(id=1), active TEXT NOT NULL, source_baseline INTEGER NOT NULL DEFAULT 1);
            """;
        command.ExecuteNonQuery();
        var id = "baseline-" + baseline.Sha256[..16];
        currentBaselineId = id;
        var revision = new LabRevision(id, null, "Production baseline", baseline.Instructions, GptLiveProtocol.Model,
            "cedar", "baseline", DateTimeOffset.UtcNow, null, 1, baseline.Sha256, baseline.Version);
        using var transaction = connection.BeginTransaction();
        if (Read<LabRevision>(connection, "revision", id, transaction) is null)
        {
            Insert(connection, "revision", id, "", 1, revision, transaction);
        }
        using var state = connection.CreateCommand();
        state.Transaction = transaction;
        state.CommandText = "INSERT OR IGNORE INTO lab_state(id,active) VALUES(1,$id)";
        state.Parameters.AddWithValue("$id", id);
        state.ExecuteNonQuery();
        using var refresh = connection.CreateCommand();
        refresh.Transaction = transaction;
        refresh.CommandText = "UPDATE lab_state SET active=$id WHERE id=1 AND source_baseline=1";
        refresh.Parameters.AddWithValue("$id", id);
        refresh.ExecuteNonQuery();
        transaction.Commit();
    }

    internal string ActiveRevisionId
    {
        get
        {
            lock (gate)
            {
                using var connection = Open();
                return Active(connection);
            }
        }
    }
    // Parent composition can use this hook for new production sessions only.
    // The source baseline deliberately returns null to preserve the existing builder.
    internal LabRevision? PublishedOverride()
    {
        lock (gate)
        {
            using var connection = Open();
            var active = Read<LabRevision>(connection, "revision", Active(connection))!;
            return active.Status == "baseline" && active.Id == currentBaselineId ? null : active;
        }
    }
    internal LabRevision[] Revisions()
    {
        lock (gate)
        {
            using var connection = Open();
            return List<LabRevision>(connection, "revision").OrderByDescending(x => x.CreatedAt).ToArray();
        }
    }
    internal LabRevision CreateRevision(LabRevisionRequest request)
    {
        LabContract.Text(request.Name, 100);
        LabContract.Text(request.Instructions, 12_000);
        if (LiveTextBudget.UpperBound(request.Instructions + "\n" + LabContract.SafetyInstructions) > 16000)
        { throw new LabException(400, "lab_instruction_limit"); }
        if (!LabContract.Engine(request.Engine) || !LabContract.Voice(request.Voice))
        {
            throw new LabException(400, "invalid_lab_configuration");
        }
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            if (request.ParentId is null || Read<LabRevision>(connection, "revision", request.ParentId, transaction) is null)
            {
                throw new LabException(404, "lab_revision_not_found");
            }
            if (Count(connection, "revision", transaction) >= 1000)
            {
                throw new LabException(409, "lab_revision_capacity");
            }
            var revision = new LabRevision(Guid.NewGuid().ToString("N"), request.ParentId, request.Name.Trim(),
                request.Instructions, request.Engine, request.Voice, "draft", DateTimeOffset.UtcNow, null,
                1, LabContract.Hash(request.Instructions), "persona-lab-v1");
            Insert(connection, "revision", revision.Id, "", revision.Revision, revision, transaction);
            transaction.Commit();
            return revision;
        }
    }
    internal LabRevision Publish(string id, string expectedActive, bool rollback = false)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var revision = Read<LabRevision>(connection, "revision", id, transaction)
                ?? throw new LabException(404, "lab_revision_not_found");
            if (rollback && revision.Status == "draft") { throw new LabException(409, "lab_revision_not_published"); }
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE lab_state SET active=$id,source_baseline=$source WHERE id=1 AND active=$expected";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$expected", expectedActive ?? "");
            command.Parameters.AddWithValue("$source", id == currentBaselineId ? 1 : 0);
            if (command.ExecuteNonQuery() != 1) { throw new LabException(409, "lab_revision_conflict"); }
            if (revision.Status != "baseline")
            {
                var next = revision with { Status = "published", PublishedAt = DateTimeOffset.UtcNow, Revision = revision.Revision + 1 };
                Update(connection, "revision", id, revision.Revision, next.Revision, next, transaction);
                revision = next;
            }
            transaction.Commit();
            return revision;
        }
    }
    internal LabSession CreateSession(string owner, LabSessionRequest request)
    {
        LabContract.Text(owner, 128);
        LabContract.Text(request.RevisionId, 100);
        LabContract.Text(request.Label, 100);
        if (!LabContract.Engine(request.Engine)
            || !LabContract.Scenarios.Any(x => x.Id == request.ScenarioId)
            || !LabContract.Fixtures.Any(x => x.Id == request.FixtureId)
            || request.ComparisonId is { } comparison && (comparison.Length > 64 || !comparison.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
        {
            throw new LabException(400, "invalid_lab_session");
        }
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var revision = Read<LabRevision>(connection, "revision", request.RevisionId, transaction)
                ?? throw new LabException(404, "lab_revision_not_found");
            if (List<LabSession>(connection, "session", owner, transaction).Length >= 1000)
            {
                throw new LabException(409, "lab_session_capacity");
            }
            var session = new LabSession(Guid.NewGuid().ToString("N"), owner, request.Label.Trim(), request.Engine,
                request.ScenarioId, request.FixtureId, request.ComparisonId, DateTimeOffset.UtcNow, null, "open",
                revision with { Engine = request.Engine }, [], [LabContract.Event("session_created", "simulation_only")], null, 1, request.RecordContent,
                LabContract.Fixtures.Single(x => x.Id == request.FixtureId), LabContract.Scenarios.Single(x => x.Id == request.ScenarioId));
            Insert(connection, "session", session.Id, owner, session.Revision, session, transaction);
            transaction.Commit();
            return session;
        }
    }
    internal LabSession[] Sessions(string owner)
    {
        lock (gate)
        {
            using var connection = Open();
            return List<LabSession>(connection, "session", owner).OrderByDescending(x => x.CreatedAt).ToArray();
        }
    }
    internal LabSession Session(string owner, string id)
    {
        lock (gate)
        {
            using var connection = Open();
            return Owned(connection, owner, id);
        }
    }
    internal LabSession Change(string owner, string id, Func<LabSession, LabSession> change)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var previous = Owned(connection, owner, id, transaction);
            var next = change(previous) with { Revision = previous.Revision + 1 };
            if (next.Turns.Length > 200 || next.Events.Length > 1000
                || next.Turns.Sum(x => x.Text.Length) > 128_000)
            {
                throw new LabException(409, "lab_session_capacity");
            }
            Update(connection, "session", id, previous.Revision, next.Revision, next, transaction);
            transaction.Commit();
            return next;
        }
    }
    internal LabSession Evaluate(string owner, string id, LabEvaluation evaluation) => Change(owner, id, session =>
    {
        if (evaluation.Scores is null || evaluation.Scores.Count != LabContract.Metrics.Length
            || LabContract.Metrics.Any(metric => !evaluation.Scores.TryGetValue(metric, out var score) || score is < 1 or > 5)
            || evaluation.TurnComments is null || evaluation.TurnComments.Length > 200)
        {
            throw new LabException(400, "invalid_lab_evaluation");
        }
        LabContract.Text(evaluation.Comment, 4000, empty: true);
        foreach (var comment in evaluation.TurnComments)
        {
            if (!session.Turns.Any(x => x.Id == comment.TurnId)) { throw new LabException(400, "invalid_lab_turn_reference"); }
            LabContract.Text(comment.Comment, 1000, empty: true);
        }
        return session with { Evaluation = evaluation };
    });
    internal void DeleteSession(string owner, string id)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Owned(connection, owner, id, transaction);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM lab_documents WHERE kind='session' AND id=$id AND owner=$owner";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$owner", owner);
            command.ExecuteNonQuery();
            // A user's explicit deletion also removes that session's suite result.
            foreach (var suite in List<LabSuite>(connection, "suite", owner, transaction).Where(x => x.Results.Any(result => result.SessionId == id)))
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE lab_documents SET json=$json,version=version+1 WHERE kind='suite' AND id=$id AND owner=$owner";
                update.Parameters.AddWithValue("$json", JsonSerializer.Serialize(suite with { Results = suite.Results.Where(result => result.SessionId != id).ToArray() }, Json));
                update.Parameters.AddWithValue("$id", suite.Id);
                update.Parameters.AddWithValue("$owner", owner);
                update.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }
    internal object Diagnostics()
    {
        lock (gate)
        {
            using var connection = Open();
            return new { sessions = Count(connection, "session"), revisions = Count(connection, "revision"), simulationOnly = true };
        }
    }
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
    private static string Active(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT active FROM lab_state WHERE id=1";
        return (string)command.ExecuteScalar()!;
    }
    private static LabSession Owned(SqliteConnection connection, string owner, string id, SqliteTransaction? transaction = null)
    {
        var session = Read<LabSession>(connection, "session", id, transaction);
        return session is not null && session.OwnerId == owner ? session : throw new LabException(404, "lab_session_not_found");
    }
    private static T? Read<T>(SqliteConnection connection, string kind, string id, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT json FROM lab_documents WHERE kind=$kind AND id=$id";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json, Json) : default;
    }
    private static T[] List<T>(SqliteConnection connection, string kind, string? owner = null, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT json FROM lab_documents WHERE kind=$kind" + (owner is null ? "" : " AND owner=$owner");
        command.Parameters.AddWithValue("$kind", kind);
        if (owner is not null) { command.Parameters.AddWithValue("$owner", owner); }
        using var reader = command.ExecuteReader();
        var results = new List<T>();
        while (reader.Read()) { results.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), Json)!); }
        return results.ToArray();
    }
    private static long Count(SqliteConnection connection, string kind, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM lab_documents WHERE kind=$kind";
        command.Parameters.AddWithValue("$kind", kind);
        return (long)command.ExecuteScalar()!;
    }
    private static void Insert<T>(SqliteConnection connection, string kind, string id, string owner, int version, T value, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO lab_documents(kind,id,owner,version,json) VALUES($kind,$id,$owner,$version,$json)";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value, Json));
        command.ExecuteNonQuery();
    }
    private static void Update<T>(SqliteConnection connection, string kind, string id, int previous, int version, T value, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE lab_documents SET json=$json,version=$version WHERE kind=$kind AND id=$id AND version=$previous";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$previous", previous);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value, Json));
        if (command.ExecuteNonQuery() != 1) { throw new LabException(409, "lab_revision_conflict"); }
    }
}
