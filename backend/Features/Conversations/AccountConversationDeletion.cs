namespace Kufr.Backend.Features.Conversations;

internal sealed partial class EveningMemoryStore
{
    internal void ScrubAccountConversation(string device, string conversation)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            // Revocation has already rebuilt current aggregate editions without this source.
            // Remove obsolete editions rather than retain private evidence in withdrawn snapshots.
            using var command = Command(connection, transaction, """
                DELETE FROM show_chronicle_editions WHERE device_id=$device AND withdrawn=1
                    AND evening_id IN (SELECT evening_id FROM show_memory_events
                        WHERE device_id=$device AND (source_id=$conversation OR conversation_id=$conversation));
                UPDATE show_memory_events SET evidence=NULL,joke_id=NULL,provenance='deleted',revoked=1
                    WHERE device_id=$device AND (source_id=$conversation OR conversation_id=$conversation);
                DELETE FROM memory_guests WHERE device_id=$device AND conversation_id=$conversation AND app_namespace=$namespace;
                DELETE FROM memory_context_scopes WHERE device_id=$device AND conversation_id=$conversation AND app_namespace=$namespace;
                """, ("$device", device), ("$conversation", conversation), ("$namespace", memoryOptions.Namespace));
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }
}
