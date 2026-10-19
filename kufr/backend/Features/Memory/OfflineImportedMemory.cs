using Kufr.Backend.Features.Show;

namespace Kufr.Backend.Features.Conversations;

internal sealed partial class EveningMemoryStore
{
    // An authenticated import describes a completed historical action, not new
    // admission to a closed evening. All its facts commit together before ACK.
    internal bool RecordImportedOfflineOutcome(string deviceId, string eveningId,
        string operationId, int slot, string provenance)
    {
        if (!ValidShowIdentifier(deviceId) || !Guid.TryParseExact(eveningId, "D", out _)
            || !Guid.TryParseExact(operationId, "D", out _) || slot is < 1 or > 6
            || provenance is not ("device_report" or "operator_relay")) { return false; }
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var catalog = Command(connection, transaction,
                "SELECT 1 FROM evening_catalog WHERE device_id=$device AND evening_id=$evening",
                ("$device", deviceId), ("$evening", eveningId));
            if (catalog.ExecuteScalar() is null || ShowSourceRevoked(connection, transaction, deviceId, operationId))
            { return false; }
            using var capture = Command(connection, transaction,
                "SELECT evening_id FROM show_visits WHERE device_id=$device AND conversation_id=$operation",
                ("$device", deviceId), ("$operation", operationId));
            var captured = capture.ExecuteScalar() as string;
            if (captured is not null && captured != eveningId) { return false; }
            if (captured is null) { InsertShowVisit(connection, transaction, deviceId, eveningId, operationId); }
            using var terminal = Command(connection, transaction,
                "SELECT 1 FROM show_memory_events WHERE device_id=$device AND conversation_id=$operation "
                + "AND kind='VisitInterrupted' AND revoked=0",
                ("$device", deviceId), ("$operation", operationId));
            if (terminal.ExecuteScalar() is not null) { return false; }
            var facts = new[]
            {
                new ShowMemoryEvent(operationId + ":started", ShowMemoryEventKind.VisitStarted,
                    deviceId, eveningId, operationId, 1, provenance, ConversationId: operationId),
                new ShowMemoryEvent(operationId + ":presentation", ShowMemoryEventKind.PresentationConfirmed,
                    deviceId, eveningId, operationId, 1, provenance, ConversationId: operationId,
                    Slot: slot, Evidence: "completed_by_timing"),
                new ShowMemoryEvent(operationId + ":completed", ShowMemoryEventKind.VisitCompleted,
                    deviceId, eveningId, operationId, 1, provenance, ConversationId: operationId)
            };
            var changed = false;
            foreach (var fact in facts)
            {
                if (ShowEventMatches(connection, transaction, fact)) { continue; }
                if (!InsertShowEvent(connection, transaction, fact)) { return false; }
                changed = true;
            }
            if (changed && ShowClosed(connection, transaction, deviceId, eveningId))
            { SaveChronicleEdition(connection, transaction, deviceId, eveningId); }
            transaction.Commit();
            return true;
        }
    }
}
