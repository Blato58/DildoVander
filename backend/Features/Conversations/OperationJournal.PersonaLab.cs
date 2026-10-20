using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Conversations;

internal sealed partial class OperationJournal
{
    private static void EnsureLabProvenance(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS persona_lab_conversations (
                conversation_id TEXT PRIMARY KEY, device_id TEXT NOT NULL,
                lab_session_id TEXT NOT NULL, lab_revision_id TEXT NOT NULL, owner_id TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS persona_lab_operations (
                operation_id TEXT PRIMARY KEY, device_id TEXT NOT NULL,
                conversation_id TEXT NOT NULL, lab_session_id TEXT NOT NULL,
                proposal_json TEXT, outcome_id TEXT, outcome_hash TEXT);
            """;
        command.ExecuteNonQuery();
    }

    private static void RecordLabCreate(SqliteConnection connection, SqliteTransaction transaction,
        string device, string conversation, string session, string revision, string owner)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO persona_lab_conversations VALUES ($conversation,$device,$session,$revision,$owner)";
        command.Parameters.AddWithValue("$conversation", conversation);
        command.Parameters.AddWithValue("$device", device);
        command.Parameters.AddWithValue("$session", session);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$owner", owner);
        command.ExecuteNonQuery();
    }

    private static void RecordLabOperation(SqliteConnection connection, SqliteTransaction transaction,
        string device, string conversation, string operation)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO persona_lab_operations(operation_id,device_id,conversation_id,lab_session_id)
            SELECT $operation,device_id,conversation_id,lab_session_id FROM persona_lab_conversations
            WHERE device_id=$device AND conversation_id=$conversation;
            """;
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$device", device);
        command.Parameters.AddWithValue("$conversation", conversation);
        command.ExecuteNonQuery();
    }

    internal bool IsLabConversation(string device, string conversation)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM persona_lab_conversations WHERE device_id=$device AND conversation_id=$conversation";
        command.Parameters.AddWithValue("$device", device);
        command.Parameters.AddWithValue("$conversation", conversation);
        return command.ExecuteScalar() is not null;
    }

    private bool? CompleteLabSelection(string operation, object? value)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM persona_lab_operations WHERE operation_id=$operation";
        command.Parameters.AddWithValue("$operation", operation);
        if (command.ExecuteScalar() is null) { return null; }
        var selection = value as SelectionProposal;
        if (selection is not null && !OperationEpoch.IsValid(selection.OperationEpoch)) { return false; }
        command.CommandText = """
            UPDATE turn_operations SET status='completed'
            WHERE operation_id=$operation AND status IN ('pending','running')
                AND ($hasSelection=0 OR COALESCE(operation_epoch,'0')=$operationEpoch);
            """;
        command.Parameters.AddWithValue("$hasSelection", selection is null ? 0 : 1);
        command.Parameters.AddWithValue("$operationEpoch", OperationEpoch.Key(selection?.OperationEpoch));
        if (command.ExecuteNonQuery() != 1) { return false; }
        command.CommandText = "UPDATE persona_lab_operations SET proposal_json=$proposal WHERE operation_id=$operation";
        command.Parameters.AddWithValue("$proposal", value is SelectionProposal proposal
            ? JsonSerializer.Serialize(proposal with { Reason = "Persona Lab hardware test." }) : DBNull.Value);
        command.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }

    internal (string OwnerId, string SessionId, SelectionProposal? Proposal)? ReadLabSelection(string device, string operation)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT c.owner_id,o.lab_session_id,o.proposal_json FROM persona_lab_operations o JOIN persona_lab_conversations c ON c.conversation_id=o.conversation_id WHERE o.device_id=$device AND o.operation_id=$operation";
        command.Parameters.AddWithValue("$device", device);
        command.Parameters.AddWithValue("$operation", operation);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null
            : JsonSerializer.Deserialize<SelectionProposal>(reader.GetString(2))) : null;
    }

    internal bool ConfirmLabOutcome(string device, string operation, string outcome, string hash)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE persona_lab_operations SET outcome_id=$outcome,outcome_hash=$hash
            WHERE device_id=$device AND operation_id=$operation AND proposal_json IS NOT NULL AND outcome_id IS NULL;
            """;
        command.Parameters.AddWithValue("$device", device);
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$outcome", outcome);
        command.Parameters.AddWithValue("$hash", hash);
        command.ExecuteNonQuery();
        command.CommandText = "SELECT 1 FROM persona_lab_operations WHERE device_id=$device AND operation_id=$operation AND outcome_id=$outcome AND outcome_hash=$hash";
        var matches = command.ExecuteScalar() is not null;
        transaction.Commit();
        return matches;
    }
}
