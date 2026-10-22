using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.PersonaLab;

internal sealed partial class PersonaLabStore
{
    private static void RequireOwner(SqliteConnection connection, SqliteTransaction transaction, string owner)
    {
        if (owner.Length == 0) { return; }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM lab_owner_fences WHERE owner=$owner";
        command.Parameters.AddWithValue("$owner", owner);
        if ((long)command.ExecuteScalar()! != 0) { throw new LabException(409, "account_deleted"); }
    }

    internal void FenceAccountDeletion(string owner)
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO lab_owner_fences(owner) VALUES($owner)";
            command.Parameters.AddWithValue("$owner", owner);
            command.ExecuteNonQuery();
        }
    }

    internal void DeleteAccountContent(string owner)
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM lab_documents WHERE owner=$owner AND owner<>''";
            command.Parameters.AddWithValue("$owner", owner);
            command.ExecuteNonQuery();
        }
    }
}
