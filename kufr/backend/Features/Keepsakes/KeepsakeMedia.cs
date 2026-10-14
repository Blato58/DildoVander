using System.Security.Cryptography;
using SkiaSharp;

namespace Kufr.Backend.Features.Keepsakes;

internal sealed partial class KeepsakeStore
{
    private static readonly SemaphoreSlim DecodeGate = new(1, 1);
    private static void ValidateGeneratedImage(KeepsakeProviderOutput output)
    {
        if (output.ImageBytes.Length is 0 or > 24 * 1024 * 1024 || output.MediaType != "image/png")
        {
            throw new KeepsakeException("provider_image_invalid");
        }
        _ = DecodePhoto(output.ImageBytes, "image/png", normalize: false);
    }

    private static (byte[] Bytes, int Width, int Height) DecodePhoto(byte[] original, string mediaType, bool normalize)
    {
        using var data = SKData.CreateCopy(original);
        using var codec = SKCodec.Create(data);
        if (codec is null) { throw new KeepsakeException("photo_decode_failed", 415); }
        var actualMime = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Jpeg => "image/jpeg",
            SKEncodedImageFormat.Png => "image/png",
            SKEncodedImageFormat.Webp => "image/webp",
            _ => throw new KeepsakeException("photo_format_not_supported", 415)
        };
        if (actualMime != mediaType) { throw new KeepsakeException("photo_mime_mismatch", 415); }
        var width = codec.Info.Width;
        var height = codec.Info.Height;
        if (width is <= 0 or > 8192 || height is <= 0 or > 8192 || (long)width * height > 24_000_000)
        {
            throw new KeepsakeException("photo_pixel_limit", 413);
        }
        if (codec.FrameCount > 1) { throw new KeepsakeException("animated_photo_not_supported", 415); }
        using var decoded = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success)
        {
            throw new KeepsakeException("photo_decode_failed", 415);
        }
        if (!normalize) { return ([], width, height); }
        var rotated = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var displayWidth = rotated ? height : width;
        var displayHeight = rotated ? width : height;
        var factor = Math.Min(1f, 2048f / Math.Max(displayWidth, displayHeight));
        var normalizedWidth = Math.Max(1, (int)Math.Round(displayWidth * factor));
        var normalizedHeight = Math.Max(1, (int)Math.Round(displayHeight * factor));
        using var normalized = new SKBitmap(new SKImageInfo(normalizedWidth, normalizedHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(normalized);
        var (sx, kx, tx, ky, sy, ty) = codec.EncodedOrigin switch
        {
            SKEncodedOrigin.TopRight => (-1f, 0f, (float)width, 0f, 1f, 0f),
            SKEncodedOrigin.BottomRight => (-1f, 0f, (float)width, 0f, -1f, (float)height),
            SKEncodedOrigin.BottomLeft => (1f, 0f, 0f, 0f, -1f, (float)height),
            SKEncodedOrigin.LeftTop => (0f, 1f, 0f, 1f, 0f, 0f),
            SKEncodedOrigin.RightTop => (0f, -1f, (float)height, 1f, 0f, 0f),
            SKEncodedOrigin.RightBottom => (0f, -1f, (float)height, -1f, 0f, (float)width),
            SKEncodedOrigin.LeftBottom => (0f, 1f, 0f, -1f, 0f, (float)width),
            _ => (1f, 0f, 0f, 0f, 1f, 0f)
        };
        var matrix = new SKMatrix
        {
            ScaleX = sx * factor, SkewX = kx * factor, TransX = tx * factor,
            SkewY = ky * factor, ScaleY = sy * factor, TransY = ty * factor, Persp2 = 1
        };
        canvas.SetMatrix(in matrix);
        canvas.DrawBitmap(decoded, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        canvas.Flush();
        // Freshly encoded pixels carry none of the original EXIF, XMP, ICC or other metadata.
        using var image = SKImage.FromBitmap(normalized);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return (encoded.ToArray(), displayWidth, displayHeight);
    }

    public int SetPortraitConsent(string id, string session, bool consent, string policyVersion, string key)
    {
        RequireFeature("PortraitEnabled");
        if (policyVersion != "dv10-portrait-v1") { throw new KeepsakeException("unknown_consent_policy"); }
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            RequireAccess(connection, transaction, id, session, KeepsakeScope.Photo);
            var scope = "consent:" + id;
            var body = consent + ":" + policyVersion;
            var replay = Replay(connection, transaction, scope, key, body);
            if (replay is not null) { return int.Parse(replay); }
            var document = Load(connection, transaction, id);
            document.PortraitConsent = consent;
            document.ConsentPolicy = policyVersion;
            document.ConsentVersion++;
            document.Revision++;
            Save(connection, transaction, id, document);
            Execute(connection, transaction, "UPDATE dv10_keepsakes SET photo_epoch=photo_epoch+1 WHERE id=$id", ("$id", id));
            // Revocation prevents new dispatch and late ingest; accepted content is retained.
            Execute(connection, transaction,
                "UPDATE dv10_jobs SET state='cancelled' WHERE keepsake_id=$id AND kind='portrait' AND state IN ('queued','leased','dispatched')", ("$id", id));
            Remember(connection, transaction, scope, key, body, document.ConsentVersion.ToString());
            transaction.Commit();
            return document.ConsentVersion;
        }
    }

    public async Task<KeepsakeUpload> AcceptPhotoAsync(string id, string session, Stream stream,
        string mediaType, string key, int consentVersion, CancellationToken cancellationToken = default)
    {
        RequireFeature("PortraitEnabled");
        lock (gate)
        {
            using var connection = Open();
            RequireAccess(connection, null, id, session, KeepsakeScope.Photo);
            RequireConsent(Load(connection, null, id), consentVersion);
            CheckAdmissionCapacity();
        }
        if (mediaType is not ("image/jpeg" or "image/png" or "image/webp"))
        {
            throw new KeepsakeException("photo_format_not_supported", 415);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var originalBuffer = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var length = await stream.ReadAsync(buffer, deadline.Token);
            if (length == 0) { break; }
            if (originalBuffer.Length + length > 12 * 1024 * 1024) { throw new KeepsakeException("photo_byte_limit", 413); }
            await originalBuffer.WriteAsync(buffer.AsMemory(0, length), deadline.Token);
        }
        var original = originalBuffer.ToArray();
        if (original.Length == 0) { throw new KeepsakeException("empty_photo"); }
        var bodyHash = Convert.ToHexString(SHA256.HashData(original)) + ":" + mediaType + ":" + consentVersion;
        byte[] normalized;
        int width;
        int height;
        await DecodeGate.WaitAsync(deadline.Token);
        try
        {
            var result = DecodePhoto(original, mediaType, normalize: true);
            normalized = result.Bytes;
            width = result.Width;
            height = result.Height;
        }
        finally { DecodeGate.Release(); }
        deadline.Token.ThrowIfCancellationRequested();
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            RequireAccess(connection, transaction, id, session, KeepsakeScope.Photo);
            var document = Load(connection, transaction, id);
            RequireConsent(document, consentVersion);
            var scope = "photo:" + id;
            var replay = Replay(connection, transaction, scope, key, bodyHash);
            if (replay is not null) { return document.Uploads.Single(item => item.Id == replay); }
            if (document.Uploads.Count >= 20) { throw new KeepsakeException("upload_capacity", 429); }
            var uploadId = NewId();
            var raw = StoreAsset(id, document, "photo_original", mediaType, original, uploadId: uploadId);
            var modelCopy = StoreAsset(id, document, "photo_normalized", "image/png", normalized, uploadId: uploadId);
            var upload = new KeepsakeUpload(uploadId, raw.Id, modelCopy.Id, mediaType, width, height, Now);
            document.Uploads.Add(upload);
            document.Revision++;
            Save(connection, transaction, id, document);
            Remember(connection, transaction, scope, key, bodyHash, uploadId);
            transaction.Commit();
            return upload;
        }
    }

    private static void RequireConsent(Document document, int version)
    {
        if (!document.PortraitConsent || version != document.ConsentVersion)
        {
            throw new KeepsakeException("portrait_consent_changed", 409);
        }
    }

    private byte[] ReadBytes(string id, KeepsakeAsset asset)
    {
        var bytes = File.ReadAllBytes(AssetPath(id, asset.Id, asset.UploadId));
        if (bytes.LongLength != asset.Length || Convert.ToHexString(SHA256.HashData(bytes)) != asset.Sha256)
        {
            throw new KeepsakeException("asset_integrity_failure", 503);
        }
        return bytes;
    }

    public KeepsakeView Read(string id, string session, bool shareOnly = false, string? shareEditionId = null)
    {
        lock (gate)
        {
            using var connection = Open();
            var sharedEdition = RequireAccess(connection, null, id, session, KeepsakeScope.Read, shareOnly, shareEditionId);
            var document = Load(connection, null, id);
            if (shareOnly)
            {
                var editions = document.Editions.Where(item => item.Id == sharedEdition).ToArray();
                var artworkIds = editions.Select(item => item.ArtworkAssetId).ToHashSet();
                var assets = document.Assets.Where(item => artworkIds.Contains(item.Id)
                    || (item.EditionId == sharedEdition && item.UploadId is null
                        && item.Kind is "export_story" or "export_card" or "export_zip")).ToArray();
                return new(id, document.Snapshot, "manual_only", "ready", editions, [], [], [], assets,
                    true, sharedEdition, document.Revision);
            }
            using var jobs = Command(connection, null, "SELECT id FROM dv10_jobs WHERE keepsake_id=$id ORDER BY created,id", ("$id", id));
            using var reader = jobs.ExecuteReader();
            var jobIds = new List<string>();
            while (reader.Read()) { jobIds.Add(reader.GetString(0)); }
            reader.Close();
            var jobViews = jobIds.Select(jobId => ReadJob(connection, null, jobId)).ToArray();
            var ready = document.Editions.Any(item => document.Assets.Any(asset => asset.Id == item.ArtworkAssetId && asset.Kind == "base_image"));
            var state = ready ? "ready" : document.Editions.Any(item => item.PublicationState == "story_ready") ? "story_ready"
                : document.NeedsBaseGeneration && document.CurrentBaseJob is null
                    ? MayDispatch("base") ? "awaiting_capacity" : "paused_preparing" : "pending";
            return new(id, document.Snapshot, "manual_only", state, document.Editions,
                document.Portraits, document.Uploads, jobViews, document.Assets, false, null, document.Revision,
                document.PortraitConsent, document.ConsentVersion);
        }
    }

    public KeepsakeAssetContent ReadAsset(string id, string session, string assetId, bool shareOnly = false, string? shareEditionId = null)
    {
        lock (gate)
        {
            var view = Read(id, session, shareOnly, shareEditionId);
            var asset = view.Assets.SingleOrDefault(item => item.Id == assetId);
            if (asset is null) { throw new KeepsakeException("asset_not_found", 404); }
            return new(asset, ReadBytes(id, asset));
        }
    }
}
