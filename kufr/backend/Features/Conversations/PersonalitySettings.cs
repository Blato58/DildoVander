namespace Kufr.Backend.Features.Conversations;

internal sealed partial class OperationJournal
{
    internal string ReadPersonality()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM server_settings WHERE name = 'personality'";
        var id = command.ExecuteScalar() as string ?? PersonalityCatalog.DefaultId;
        if (!PersonalityCatalog.Contains(id))
            throw new InvalidOperationException("Stored personality is not in the inventory.");
        return id;
    }

    internal void WritePersonality(string id)
    {
        if (!PersonalityCatalog.Contains(id)) throw new ArgumentException("Unknown personality.", nameof(id));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO server_settings (name, value) VALUES ('personality', $id)
            ON CONFLICT(name) DO UPDATE SET value = excluded.value
            """;
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }
}
