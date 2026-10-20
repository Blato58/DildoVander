using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Conversations;

// Technical selection evidence survives the conversation's short-lived journal.
// No dialogue, reasoning or visitor photo is copied into this outbox.
internal sealed partial class OperationJournal
{
    private bool keepsakesEnabled;

    public bool MarkSelectionCompleted(string operationId, object? value, string? eveningId = null,
        long memoryDeletionEpoch = 0, string personalityId = PersonalityCatalog.DefaultId)
    {
        if (!keepsakesEnabled || value is not SelectionProposal proposal)
        {
            return MarkCompleted(operationId);
        }
        using var connection = Open();
        EnsureKeepsakeOutbox(connection);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE turn_operations SET status = 'completed'
            WHERE operation_id = $operation AND status IN ('pending', 'running');
            """;
        command.Parameters.AddWithValue("$operation", operationId);
        if (command.ExecuteNonQuery() != 1)
        {
            return false;
        }
        command.CommandText = """
            INSERT INTO keepsake_selection_outbox(operation_id, device_id, proposal_json, evening_id, memory_deletion_epoch,personality_id)
            SELECT operation_id, device_id, $proposal, $evening, $epoch,$personality FROM turn_operations
            WHERE operation_id = $operation;
            """;
        command.Parameters.AddWithValue("$proposal", JsonSerializer.Serialize(proposal with { Reason = "" }));
        command.Parameters.AddWithValue("$evening", eveningId is null ? DBNull.Value : eveningId);
        command.Parameters.AddWithValue("$epoch", memoryDeletionEpoch);
        command.Parameters.AddWithValue("$personality", personalityId);
        command.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }

    public string? ReadKeepsakeEvening(string deviceId, string operationId)
    {
        if (!keepsakesEnabled) { return null; }
        using var connection = Open();
        EnsureKeepsakeOutbox(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT evening_id FROM keepsake_selection_outbox WHERE operation_id=$op AND device_id=$device AND deleted=0";
        command.Parameters.AddWithValue("$op", operationId);
        command.Parameters.AddWithValue("$device", deviceId);
        return command.ExecuteScalar() as string;
    }

    public SelectionProposal? ReadKeepsakeSelection(string deviceId, string operationId)
    {
        if (!keepsakesEnabled) { return null; }
        using var connection = Open();
        EnsureKeepsakeOutbox(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT proposal_json FROM keepsake_selection_outbox WHERE operation_id=$op AND device_id=$device AND deleted=0";
        command.Parameters.AddWithValue("$op", operationId);
        command.Parameters.AddWithValue("$device", deviceId);
        var json = command.ExecuteScalar() as string;
        return json is null ? null : JsonSerializer.Deserialize<SelectionProposal>(json);
    }

    internal bool HasPendingKeepsakeSelection(string deviceId, string conversationId)
    {
        if (!keepsakesEnabled) { return false; }
        using var connection = Open();
        EnsureKeepsakeOutbox(connection);
        using var command = connection.CreateCommand();
        // A committed device outcome can still be in flight to the separate
        // memory store. Cleanup must not interrupt that narrow delivery gap.
        command.CommandText = "SELECT 1 FROM keepsake_selection_outbox WHERE device_id=$device AND deleted=0 "
            + "AND json_extract(proposal_json,'$.ConversationId')=$conversation LIMIT 1";
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$conversation", conversationId);
        return command.ExecuteScalar() is not null;
    }

    public string ConfirmKeepsakeOutcome(string deviceId, string operationId, string outcomeId,
        string evidence, string bodyHash)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE keepsake_selection_outbox SET outcome_id=$outcome, evidence=$evidence, body_hash=$hash
            WHERE operation_id=$op AND device_id=$device AND deleted=0 AND outcome_id IS NULL;
            """;
        command.Parameters.AddWithValue("$outcome", outcomeId);
        command.Parameters.AddWithValue("$evidence", evidence);
        command.Parameters.AddWithValue("$hash", bodyHash);
        command.Parameters.AddWithValue("$op", operationId);
        command.Parameters.AddWithValue("$device", deviceId);
        command.ExecuteNonQuery();
        command.CommandText = "SELECT outcome_id,body_hash FROM keepsake_selection_outbox WHERE operation_id=$op AND device_id=$device AND deleted=0";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0) || reader.GetString(1) != bodyHash)
        {
            throw new Kufr.Backend.Features.Keepsakes.KeepsakeException("outcome_conflict", 409);
        }
        var result = reader.GetString(0);
        reader.Close();
        transaction.Commit();
        return result;
    }

    private static void EnsureKeepsakeOutbox(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS keepsake_selection_outbox (
                operation_id TEXT PRIMARY KEY, device_id TEXT NOT NULL,
                proposal_json TEXT NOT NULL, outcome_id TEXT, evidence TEXT,
                body_hash TEXT, deleted INTEGER NOT NULL DEFAULT 0, evening_id TEXT,
                memory_deletion_epoch INTEGER NOT NULL DEFAULT 0,personality_id TEXT NOT NULL DEFAULT 'party-original');
            """;
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA table_info(keepsake_selection_outbox)";
        using var reader = command.ExecuteReader();
        var hasEvening = false;
        var hasMemoryEpoch = false;
        var hasPersonality = false;
        while (reader.Read())
        {
            hasEvening |= reader.GetString(1) == "evening_id";
            hasMemoryEpoch |= reader.GetString(1) == "memory_deletion_epoch";
            hasPersonality |= reader.GetString(1) == "personality_id";
        }
        reader.Close();
        if (!hasEvening)
        {
            command.CommandText = "ALTER TABLE keepsake_selection_outbox ADD COLUMN evening_id TEXT";
            command.ExecuteNonQuery();
        }
        if (!hasMemoryEpoch)
        {
            command.CommandText = "ALTER TABLE keepsake_selection_outbox ADD COLUMN memory_deletion_epoch INTEGER NOT NULL DEFAULT 0";
            command.ExecuteNonQuery();
        }
        if (!hasPersonality)
        {
            command.CommandText = "ALTER TABLE keepsake_selection_outbox ADD COLUMN personality_id TEXT NOT NULL DEFAULT 'party-original'";
            command.ExecuteNonQuery();
        }
    }

    internal long? ReadSelectionMemoryEpoch(string deviceId, string operationId)
    {
        using var connection = Open();
        EnsureKeepsakeOutbox(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT memory_deletion_epoch FROM keepsake_selection_outbox WHERE operation_id=$op AND device_id=$device AND deleted=0";
        command.Parameters.AddWithValue("$op", operationId);
        command.Parameters.AddWithValue("$device", deviceId);
        var value = command.ExecuteScalar();
        return value is long epoch ? epoch : null;
    }

    internal string ReadSelectionPersonality(string deviceId, string operationId)
    {
        using var connection = Open();
        EnsureKeepsakeOutbox(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT personality_id FROM keepsake_selection_outbox WHERE operation_id=$op AND device_id=$device AND deleted=0";
        command.Parameters.AddWithValue("$op", operationId);
        command.Parameters.AddWithValue("$device", deviceId);
        return command.ExecuteScalar() as string ?? PersonalityCatalog.DefaultId;
    }
}
