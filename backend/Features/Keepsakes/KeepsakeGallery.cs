using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Keepsakes;

internal sealed record KeepsakeGalleryEvening(string Id, string Label = "Aktivní večer");
internal sealed record KeepsakeGalleryStory(string Title, string Text);
internal sealed record KeepsakeGalleryAsset(string Id, string Kind, string MediaType, long Length, string Sha256);
internal sealed record KeepsakeGalleryItem(string Locator, string EditionId, string OfficialName, string CatalogId,
    int Slot, DateTimeOffset CreatedAt, KeepsakeGalleryStory Story, bool Synthetic, IReadOnlyList<KeepsakeGalleryAsset> Assets, WizardIdentityCard? WizardIdentity = null);
internal sealed record KeepsakeGalleryPage(int SchemaVersion, KeepsakeGalleryEvening? Evening,
    KeepsakeGalleryItem? Latest, IReadOnlyList<KeepsakeGalleryItem> Items, string? NextCursor);

internal sealed partial class KeepsakeStore
{
    private sealed record GalleryCursor(string EveningId, long PublishedAtTicks, string Locator, string EditionId);

    internal KeepsakeGalleryPage ReadEveningGallery(string deviceId, string? eveningId, int limit = 24, string? cursor = null)
    {
        if (limit is < 1 or > 48) { throw new KeepsakeException("invalid_gallery_limit"); }
        if (eveningId is null) { return new(1, null, null, [], null); }
        GalleryCursor? position = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            try
            {
                if (cursor.Length > 512) { throw new FormatException(); }
                var encoded = cursor.Replace('-', '+').Replace('_', '/');
                position = JsonSerializer.Deserialize<GalleryCursor>(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')), Json);
                if (position is null || position.EveningId != eveningId || !Guid.TryParseExact(position.Locator, "D", out _)
                    || !Guid.TryParseExact(position.EditionId, "D", out _) || position.PublishedAtTicks <= 0)
                { throw new FormatException(); }
            }
            catch (Exception exception) when (exception is FormatException or JsonException)
            {
                throw new KeepsakeException("gallery_cursor_changed", 409);
            }
        }
        lock (gate)
        {
            using var connection = Open();
            var latest = ReadGalleryRows(connection, deviceId, eveningId, 1, null).FirstOrDefault();
            var rows = ReadGalleryRows(connection, deviceId, eveningId, limit + 1, position);
            var page = rows.Take(limit).ToArray();
            var last = page.LastOrDefault();
            var next = rows.Count > limit && last is not null
                ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new GalleryCursor(eveningId,
                    last.CreatedAt.UtcTicks, last.Locator, last.EditionId), Json)).TrimEnd('=').Replace('+', '-').Replace('/', '_') : null;
            return new(1, new(eveningId), latest, page, next);
        }
    }

    private static List<KeepsakeGalleryItem> ReadGalleryRows(SqliteConnection connection, string deviceId,
        string eveningId, int limit, GalleryCursor? position)
    {
        using var command = Command(connection, null,
            "SELECT id,document,published_edition_id,published_at_ticks FROM dv10_keepsakes "
            + "WHERE device_id=$device AND evening_id=$evening AND deleted=0 AND published_at_ticks IS NOT NULL "
            + "AND ($cursor=0 OR published_at_ticks<$time OR (published_at_ticks=$time AND id<$id)) "
            + "ORDER BY published_at_ticks DESC,id DESC LIMIT $limit",
            ("$device", deviceId), ("$evening", eveningId), ("$cursor", position is null ? 0 : 1),
            ("$time", position?.PublishedAtTicks ?? 0), ("$id", position?.Locator ?? ""), ("$limit", limit));
        using var reader = command.ExecuteReader();
        var results = new List<KeepsakeGalleryItem>();
        while (reader.Read())
        {
            var document = JsonSerializer.Deserialize<Document>(reader.GetString(1), Json)!;
            var edition = document.Editions.SingleOrDefault(item => item.Id == reader.GetString(2) && item.PublicationState == "ready");
            if (edition is null || document.Snapshot.EveningId != eveningId || document.Snapshot.DeviceId != deviceId
                || !document.Assets.Any(asset => asset.Id == edition.ArtworkAssetId && asset.Kind == "base_image" && asset.UploadId is null))
            { continue; }
            var assets = document.Assets.Where(asset => IsPublicBaseAsset(asset, edition))
                .Select(asset => new KeepsakeGalleryAsset(asset.Id, asset.Kind, asset.MediaType, asset.Length, asset.Sha256)).ToArray();
            results.Add(new(reader.GetString(0), edition.Id, document.Snapshot.OfficialName, document.Snapshot.CatalogId,
                document.Snapshot.Slot, new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero),
                new(edition.Story.Title, edition.Story.Text), edition.Synthetic, assets, document.WizardIdentities.Where(item => item.EditionId == edition.Id).Select(ToCard).SingleOrDefault()));
        }
        return results;
    }

    private static bool IsPublicBaseAsset(KeepsakeAsset asset, KeepsakeEdition edition) =>
        asset.UploadId is null && asset.EditionId == edition.Id
        && (asset.Id == edition.ArtworkAssetId && asset.Kind == "base_image"
            || asset.Kind is "export_story" or "export_card" or "export_zip");

    private KeepsakeAsset RequireGalleryAsset(SqliteConnection connection, string deviceId, string eveningId,
        string id, string editionId, string assetId)
    {
        using var lookup = Command(connection, null,
            "SELECT 1 FROM dv10_keepsakes WHERE id=$id AND device_id=$device AND evening_id=$evening AND deleted=0 "
            + "AND published_edition_id=$edition AND published_at_ticks IS NOT NULL",
            ("$id", id), ("$device", deviceId), ("$evening", eveningId), ("$edition", editionId));
        if (lookup.ExecuteScalar() is null) { throw new KeepsakeException("asset_not_found", 404); }
        var document = Load(connection, null, id);
        if (document.Snapshot.DeviceId != deviceId || document.Snapshot.EveningId != eveningId)
        { throw new KeepsakeException("asset_not_found", 404); }
        var edition = document.Editions.SingleOrDefault(item => item.Id == editionId && item.PublicationState == "ready");
        var asset = document.Assets.SingleOrDefault(item => item.Id == assetId);
        if (edition is null || asset is null || !IsPublicBaseAsset(asset, edition))
        { throw new KeepsakeException("asset_not_found", 404); }
        return asset;
    }

    internal (KeepsakeGalleryAsset Asset, FileStream Stream) OpenGalleryAsset(string deviceId, string eveningId,
        string id, string editionId, string assetId)
    {
        lock (gate)
        {
            using var connection = Open();
            var asset = RequireGalleryAsset(connection, deviceId, eveningId, id, editionId, assetId);
            if (asset.Length is <= 0 or > 32L * 1024 * 1024) { throw new KeepsakeException("asset_size_limit", 413); }
            var stream = new FileStream(AssetPath(id, asset.Id), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != asset.Length) { stream.Dispose(); throw new KeepsakeException("asset_integrity_failure", 503); }
            return (new(asset.Id, asset.Kind, asset.MediaType, asset.Length, asset.Sha256), stream);
        }
    }

    internal bool IsGalleryAssetVisible(string deviceId, string eveningId, string id, string editionId, string assetId)
    {
        lock (gate)
        {
            using var connection = Open();
            try { RequireGalleryAsset(connection, deviceId, eveningId, id, editionId, assetId); return true; }
            catch (KeepsakeException) { return false; }
        }
    }
}
