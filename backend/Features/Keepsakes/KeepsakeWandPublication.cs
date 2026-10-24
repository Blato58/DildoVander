using System.Text.Json;
using Kufr.Backend.Features.Wands;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Keepsakes;

internal sealed partial class KeepsakeStore
{
    private static void ValidateGeneratedAssignment(SqliteConnection connection, SqliteTransaction transaction,
        KeepsakeIssueSnapshot snapshot, KeepsakeCustomerOwner? owner)
    {
        if (snapshot.GeneratedWand is not { } wand) return;
        using var exists = Command(connection, transaction,
            "SELECT 1 FROM sqlite_master WHERE type='table' AND name='wand_generation_sessions'");
        if (exists.ExecuteScalar() is null) throw new KeepsakeException("personal_wand_unavailable", 409);
        using var command = Command(connection, transaction, """
            SELECT selected_json FROM wand_generation_sessions WHERE state='selected'
                AND json_extract(selected_json,'$.wandId')=$wand AND ($owner IS NULL OR account_id=$owner)
            """, ("$wand", wand.WandId), ("$owner", owner?.AccountId));
        var json = command.ExecuteScalar() as string;
        var selected = json is null ? null : JsonSerializer.Deserialize<GeneratedWandSnapshot>(json, Json);
        if (selected is null || selected.PhysicalSlot != snapshot.Slot || snapshot.CatalogId != wand.WandId
            || snapshot.OfficialName != wand.Name
            || JsonSerializer.Serialize(selected.ToPublic(), Json) != JsonSerializer.Serialize(wand, Json))
            throw new KeepsakeException("personal_wand_unavailable", 409);
    }

    private static bool WandPublicationApproved(Document document, string? editionId) =>
        document.Snapshot.GeneratedWand is null || editionId is not null && document.WandPublicationEditionId == editionId;

    private static bool WandPublicationApproved(SqliteConnection connection, string id, Document document,
        SqliteTransaction? transaction = null)
    {
        using var command = Command(connection, transaction,
            "SELECT published_edition_id FROM dv10_keepsakes WHERE id=$id AND deleted=0", ("$id", id));
        return WandPublicationApproved(document, command.ExecuteScalar() as string);
    }

    internal KeepsakeView SetWandPublication(string id, KeepsakeActor actor, bool approved, string editionId, int revision)
    {
        lock (gate)
        {
            if (!actor.IsAccount) throw new KeepsakeException("account_required", 401);
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            RequireAccess(connection, transaction, id, actor, KeepsakeScope.Write);
            var document = Load(connection, transaction, id);
            if (document.Snapshot.GeneratedWand is null) throw new KeepsakeException("personal_wand_unavailable", 409);
            if (document.Revision != revision) throw new KeepsakeException("revision_conflict", 409);
            using var published = Command(connection, transaction,
                "SELECT published_edition_id FROM dv10_keepsakes WHERE id=$id AND deleted=0", ("$id", id));
            var currentEdition = published.ExecuteScalar() as string;
            if (approved && (currentEdition != editionId || !document.Editions.Any(e => e.Id == editionId && e.PublicationState == "ready")))
                throw new KeepsakeException("edition_not_ready", 409);
            document.WandPublicationEditionId = approved ? editionId : null;
            document.Revision++;
            Save(connection, transaction, id, document);
            transaction.Commit();
            return Read(id, actor);
        }
    }
}
