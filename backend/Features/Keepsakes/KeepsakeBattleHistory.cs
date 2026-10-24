using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Keepsakes;

internal sealed partial class KeepsakeStore
{
    internal IReadOnlyList<string> OwnedBattleIdentities(KeepsakeActor actor)
    {
        if (!actor.IsAccount) throw new KeepsakeException("account_required", 401);
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            RequireSession(connection, transaction, actor);
            using var command = Command(connection, transaction, """
                SELECT json_extract(i.value,'$.identityId') FROM dv10_customer_owners o
                JOIN dv10_keepsakes k ON k.id=o.keepsake_id,
                    json_each(k.document,'$.wizardIdentities') i
                WHERE o.owner_id=$owner AND k.deleted=0 AND o.epoch=k.account_access_epoch
                LIMIT 4097
                """, ("$owner", actor.AccountId));
            var ids = new List<string>();
            using (var reader = command.ExecuteReader())
                while (reader.Read()) ids.Add(reader.GetString(0));
            if (ids.Count > 4096) throw new KeepsakeException("battle_inventory_limit", 413);
            transaction.Commit();
            return ids;
        }
    }
}
