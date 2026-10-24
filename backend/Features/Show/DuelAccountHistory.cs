namespace Kufr.Backend.Features.Show;

internal sealed partial class DuelStore
{
    internal IReadOnlyList<DuelView> AccountHistory(IReadOnlyList<string> ownedIdentityIds, string? id = null)
    {
        // Identity IDs come only from validated original-owner archive rows. Using
        // the six legacy wand IDs here would accidentally join unrelated guests.
        if (ownedIdentityIds.Count == 0) return [];
        var allowed = ownedIdentityIds.ToHashSet(StringComparer.Ordinal);
        using var connection = Open();
        using var command = Command(connection, null, """
            SELECT document FROM show_duels
            WHERE ($id IS NULL OR id=$id) AND (
                json_extract(document,'$.left.identityId') IN (SELECT value FROM json_each($owned)) OR
                json_extract(document,'$.right.identityId') IN (SELECT value FROM json_each($owned)))
            ORDER BY rowid DESC LIMIT 40
            """, ("$id", id), ("$owned", Serialize(allowed)));
        using var reader = command.ExecuteReader();
        var result = new List<DuelView>();
        while (reader.Read())
        {
            var duel = Deserialize<DuelDocument>(reader.GetString(0));
            result.Add(duel.View());
        }
        return result;
    }
}
