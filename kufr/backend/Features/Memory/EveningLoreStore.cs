using Kufr.Backend.Features.Show;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Conversations;

internal sealed partial class EveningMemoryStore
{
    private static bool IsKnownJoke(string? id) => id is "ministry_queue" or "ministry_rivalry"
        || id is { Length: 11 } && id.StartsWith("wand_room_", StringComparison.Ordinal) && id[^1] is >= '1' and <= '6';

    private static bool HasJokeFacts(ChronicleSnapshot snapshot, string id) => snapshot.Provisional && id switch
    {
        "ministry_queue" => snapshot.CompletedVisits > 0,
        "ministry_rivalry" => snapshot.CompletedDuels > 0,
        _ => IsKnownJoke(id) && snapshot.Wands.Any(w => w.Slot == id[^1] - '0' && w.ConfirmedPresentations > 0)
    };

    // Local bounded allowlist only. No retrieval, provider, transcript or alias is
    // read here; failure of the optional index cannot delay a normal voice turn.
    internal EveningLore BuildEveningLore(string deviceId, string eveningId)
    {
        lock (gate)
        {
            using var connection = Open();
            var snapshot = ReadChronicle(deviceId, eveningId);
            if (snapshot is null || !snapshot.Provisional)
            {
                return new(1, eveningId, []);
            }
            var motifs = new List<EveningLoreItem>();
            foreach (var wand in snapshot.Wands.Where(w => w.ConfirmedPresentations > 0)
                .OrderByDescending(w => w.ConfirmedPresentations).ThenBy(w => w.Slot))
            {
                Add($"wand_room_{wand.Slot}", $"{wand.OfficialName} má dnes potvrzené prezentace: {wand.ConfirmedPresentations}. Už žádá vlastní šatnu.");
            }
            if (snapshot.CompletedVisits > 0)
            {
                Add("ministry_queue", $"Ministerstvo dnes odbavilo dokončené návštěvy: {snapshot.CompletedVisits}. Razítko se dožaduje dovolené.");
            }
            if (snapshot.CompletedDuels > 0)
            {
                Add("ministry_rivalry", $"Večerní kniha eviduje dokončené duely: {snapshot.CompletedDuels}. Katalogové postavy už se přou o služební koště.");
            }
            return new(1, eveningId, motifs.Take(3).ToArray());

            void Add(string id, string text)
            {
                var (uses, eligible) = JokeAvailability(connection, null, deviceId, eveningId, id);
                if (eligible && motifs.Count < 3)
                {
                    motifs.Add(new(id, text, uses, 3, 3));
                }
            }
        }
    }

    internal bool RecordEveningJokeUse(string deviceId, string eveningId, string conversationId, string jokeId)
        => RecordShowEvent(new($"joke:{deviceId}:{conversationId}:{jokeId}", ShowMemoryEventKind.RunningJokeUsed,
            deviceId, eveningId, $"{conversationId}:{jokeId}", 1, "director_documented_use", conversationId, JokeId: jokeId));

    private static (int Uses, bool Eligible) JokeAvailability(SqliteConnection connection, SqliteTransaction? transaction,
        string device, string evening, string joke)
    {
        using var count = Command(connection, transaction, """
            SELECT COUNT(*),MAX(v.sequence) FROM show_memory_events e
            LEFT JOIN show_visits v ON v.device_id=e.device_id AND v.conversation_id=e.conversation_id
            WHERE e.device_id=$device AND e.evening_id=$evening AND e.kind='RunningJokeUsed' AND e.joke_id=$joke AND e.revoked=0
            """, ("$device", device), ("$evening", evening), ("$joke", joke));
        int uses;
        long? lastVisit;
        using (var reader = count.ExecuteReader())
        {
            reader.Read();
            uses = reader.GetInt32(0);
            lastVisit = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        }
        if (uses >= 3) { return (uses, false); }
        if (lastVisit is null) { return (uses, true); }
        using var since = Command(connection, transaction, """
            SELECT COUNT(*) FROM show_visits WHERE device_id=$device AND evening_id=$evening AND sequence>$last
            """, ("$device", device), ("$evening", evening), ("$last", lastVisit.Value));
        return (uses, Convert.ToInt32(since.ExecuteScalar()) >= 4);
    }
}
