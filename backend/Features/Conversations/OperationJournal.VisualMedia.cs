using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Conversations;

internal sealed record VisualMediaRecord(string ImageId, string DeviceId, string ConversationId,
    int Revision, string Source, DateTimeOffset CapturedAt, byte[] Jpeg,
    string? EveningId = null, string? GuestId = null);

internal sealed partial class OperationJournal
{
    internal void InitializeVisualMedia()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS visual_deleted_conversations(device_id TEXT NOT NULL,conversation_id TEXT NOT NULL,PRIMARY KEY(device_id,conversation_id));
            CREATE TABLE IF NOT EXISTS visual_media (
                image_id TEXT PRIMARY KEY, device_id TEXT NOT NULL, conversation_id TEXT NOT NULL,
                revision INTEGER NOT NULL, source TEXT NOT NULL, captured_at TEXT NOT NULL,
                jpeg BLOB NOT NULL, description TEXT, deleted INTEGER NOT NULL DEFAULT 0,
                evening_id TEXT, guest_id TEXT);
            CREATE INDEX IF NOT EXISTS visual_media_conversation ON visual_media(device_id, conversation_id, revision);
            """;
        command.ExecuteNonQuery();
    }

    internal bool ArchiveVisualMedia(VisualMediaRecord media)
    {
        InitializeVisualMedia();
        using var connection = Open();
        using var command = connection.CreateCommand();
        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM visual_deleted_conversations WHERE device_id=$device AND conversation_id=$conversation";
        command.Parameters.AddWithValue("$device", media.DeviceId);
        command.Parameters.AddWithValue("$conversation", media.ConversationId);
        if ((long)command.ExecuteScalar()! != 0) { return false; }
        command.Parameters.Clear();
        // Never resurrect an explicitly deleted image through an upload retry.
        command.CommandText = """
            INSERT OR IGNORE INTO visual_media(image_id,device_id,conversation_id,revision,source,captured_at,jpeg,evening_id,guest_id)
            VALUES($id,$device,$conversation,$revision,$source,$captured,$jpeg,$evening,$guest);
            """;
        command.Parameters.AddWithValue("$id", media.ImageId);
        command.Parameters.AddWithValue("$device", media.DeviceId);
        command.Parameters.AddWithValue("$conversation", media.ConversationId);
        command.Parameters.AddWithValue("$revision", media.Revision);
        command.Parameters.AddWithValue("$source", media.Source);
        command.Parameters.AddWithValue("$captured", media.CapturedAt.ToString("O"));
        command.Parameters.AddWithValue("$jpeg", media.Jpeg);
        command.Parameters.AddWithValue("$evening", (object?)media.EveningId ?? DBNull.Value);
        command.Parameters.AddWithValue("$guest", (object?)media.GuestId ?? DBNull.Value);
        command.ExecuteNonQuery();
        transaction.Commit();
        var existing = ReadVisualMedia(media.DeviceId, media.ConversationId, media.ImageId);
        return existing is not null && existing.Revision == media.Revision
            && existing.Source == media.Source && existing.Jpeg.AsSpan().SequenceEqual(media.Jpeg);
    }

    internal VisualMediaRecord? ReadVisualMedia(string deviceId, string conversationId, string imageId)
    {
        InitializeVisualMedia();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT revision,source,captured_at,jpeg FROM visual_media
            WHERE image_id=$id AND device_id=$device AND conversation_id=$conversation AND deleted=0;
            """;
        command.Parameters.AddWithValue("$id", imageId);
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$conversation", conversationId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(imageId, deviceId, conversationId, reader.GetInt32(0), reader.GetString(1),
            DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture), (byte[])reader[3]) : null;
    }

    internal bool CompleteVisualMedia(string deviceId, string conversationId, string imageId, int revision, string description)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE visual_media SET description=$description
            WHERE image_id=$id AND device_id=$device AND conversation_id=$conversation AND revision=$revision AND deleted=0;
            """;
        command.Parameters.AddWithValue("$id", imageId);
        command.Parameters.AddWithValue("$description", description);
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$revision", revision);
        return command.ExecuteNonQuery() == 1;
    }

    internal void DeleteVisualMedia(string deviceId, string conversationId)
    {
        InitializeVisualMedia();
        using var connection = Open();
        using var command = connection.CreateCommand();
        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        // Retain a technical tombstone, erase user content, and fence late completion.
        command.CommandText = """
            INSERT OR IGNORE INTO visual_deleted_conversations(device_id,conversation_id) VALUES($device,$conversation);
            UPDATE visual_media SET deleted=1,jpeg=X'',description=NULL
            WHERE device_id=$device AND conversation_id=$conversation;
            """;
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    internal IReadOnlyList<string> VisualConversations(string deviceId, string scope, string subject)
    {
        InitializeVisualMedia();
        var column = scope switch { "conversation" => "conversation_id", "evening" => "evening_id", "guest" => "guest_id",
            _ => throw new ArgumentException("Unsupported visual deletion scope.") };
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT DISTINCT conversation_id FROM visual_media WHERE device_id=$device AND {column}=$subject;";
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$subject", subject);
        using var reader = command.ExecuteReader();
        var conversations = new List<string>();
        while (reader.Read()) { conversations.Add(reader.GetString(0)); }
        return conversations;
    }
}
