using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using SkiaSharp;
using Kufr.Backend.Shared;

namespace Kufr.Backend.Features.Keepsakes;

internal sealed partial class KeepsakeStore
{
    // Self-contained export chrome follows Vapour Chrome; artwork bytes keep their own colors.
    private const string CardMidnight = DesignTokens.ChromeMidnight;
    private const string CardMist = DesignTokens.VapourMist;
    private const string CardSteel = DesignTokens.SteelGrey;
    private const string CardPeriwinkle = DesignTokens.DigitalPeriwinkle;

    public KeepsakeAsset CreateExport(string id, KeepsakeActor session, string editionId, string format,
        string? portraitId, string key)
    {
        if (format is not ("story" or "card" or "zip")) { throw new KeepsakeException("export_format_not_supported"); }
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            RequireAccess(connection, transaction, id, session, KeepsakeScope.Write);
            var document = Load(connection, transaction, id);
            var body = editionId + ":" + format + ":" + portraitId;
            var scope = "export:" + id;
            var replay = Replay(connection, transaction, scope, key, body);
            if (replay is not null) { return document.Assets.Single(item => item.Id == replay); }
            var result = CreateExportInternal(connection, transaction, id, document, editionId, format, portraitId);
            Remember(connection, transaction, scope, key, body, result.Id);
            transaction.Commit();
            return result;
        }
    }

    private KeepsakeAsset CreateExportInternal(Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction, string id, Document document,
        string editionId, string format, string? portraitId)
    {
        var edition = document.Editions.SingleOrDefault(item => item.Id == editionId);
        if (edition is null) { throw new KeepsakeException("edition_not_found", 404); }
        if (edition.PublicationState == "private_history") { throw new KeepsakeException("edition_not_published", 409); }
        if (portraitId is null)
        {
            var existing = document.Assets.FirstOrDefault(asset => asset.EditionId == editionId
                && asset.Kind == "export_" + format && asset.UploadId is null);
            if (existing is not null) { return existing; }
        }
        var artwork = document.Assets.SingleOrDefault(item => item.Id == edition.ArtworkAssetId);
        if (artwork is not null && artwork.Kind != "base_image") { throw new KeepsakeException("edition_not_published", 409); }
        var identity = document.WizardIdentities.Where(item => item.EditionId == editionId).Select(ToCard).SingleOrDefault();
        if (format == "card" && artwork is null && identity is null) { throw new KeepsakeException("artwork_not_ready", 409); }
        KeepsakePortrait? portrait = null;
        byte[]? portraitBytes = null;
        KeepsakeAsset? portraitAsset = null;
        if (portraitId is not null)
        {
            portrait = document.Portraits.SingleOrDefault(item => item.Id == portraitId && item.EditionId == editionId);
            if (portrait is null) { throw new KeepsakeException("portrait_not_found", 404); }
            var asset = document.Assets.Single(item => item.Id == portrait.AssetId);
            if (asset.Kind != "portrait_image") { throw new KeepsakeException("portrait_not_published", 409); }
            portraitAsset = asset;
        }
        // Reject large source bundles before allocating their bytes or a ZIP buffer.
        if ((artwork?.Length ?? 0) + (portraitAsset?.Length ?? 0) > 32L * 1024 * 1024)
        {
            throw new KeepsakeException("export_size_limit", 413);
        }
        if (portraitAsset is not null) { portraitBytes = ReadBytes(id, portraitAsset); }
        var artworkBytes = artwork is null ? null : ReadBytes(id, artwork);
        var storyBytes = Encoding.UTF8.GetBytes(StoryText(document.Snapshot, edition, identity));
        var storedCard = portraitId is null ? document.Assets.FirstOrDefault(asset => asset.EditionId == editionId
            && asset.Kind == "export_card" && asset.UploadId is null) : null;
        var cardBytes = format == "story" || (artworkBytes is null && identity is null) ? null
            : storedCard is null ? BuildPngCard(document.Snapshot, edition, artworkBytes, portraitBytes, identity) : ReadBytes(id, storedCard);
        byte[] output;
        string mime;
        switch (format)
        {
            case "story": output = storyBytes; mime = "text/plain; charset=utf-8"; break;
            case "card": output = cardBytes!; mime = "image/png"; break;
            default:
                var components = new Dictionary<string, byte[]>
                {
                    ["story.txt"] = storyBytes
                };
                if (cardBytes is not null) { components["keepsake-card.png"] = cardBytes; }
                if (artworkBytes is not null) { components["wand.png"] = artworkBytes; }
                if (portraitBytes is not null) { components["portrait.png"] = portraitBytes; }
                var manifest = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schema = "dv10-export-v1", synthetic = edition.Synthetic, portraitIncluded = portraitBytes is not null,
                    partial = artworkBytes is null, missingComponents = artworkBytes is null
                        ? cardBytes is null ? new[] { "wand.png", "keepsake-card.png" } : new[] { "wand.png" } : [],
                    components = components.Select(item => new
                    {
                        filename = item.Key, length = item.Value.LongLength,
                        sha256 = Convert.ToHexString(SHA256.HashData(item.Value))
                    })
                });
                components["manifest.json"] = manifest;
                if (components.Count > 8 || components.Sum(item => item.Value.LongLength) > 40L * 1024 * 1024)
                {
                    throw new KeepsakeException("export_size_limit", 413);
                }
                using (var stream = new MemoryStream())
                {
                    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        foreach (var item in components) { AddEntry(zip, item.Key, item.Value); }
                    }
                    output = stream.ToArray();
                }
                mime = "application/zip";
                break;
        }
        var kind = artworkBytes is null && (format is "zip" or "card") ? "export_partial_" + format : "export_" + format;
        var result = StoreAsset(id, document, kind, mime, output, editionId, portrait?.UploadId);
        Save(connection, transaction, id, document);
        return result;
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] content)
    {
        using var entry = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
        entry.Write(content);
    }

    internal static byte[] BuildPngCard(KeepsakeIssueSnapshot snapshot, KeepsakeEdition edition,
        byte[]? artwork, byte[]? portrait, WizardIdentityCard? identity = null)
    {
        using var fontStream = typeof(KeepsakeStore).Assembly.GetManifestResourceStream("Kufr.Backend.KeepsakeInter")
            ?? throw new InvalidOperationException("Keepsake font missing.");
        using var typeface = SKTypeface.FromStream(fontStream);
        using var heading = new SKFont(typeface, 42);
        using var titleFont = new SKFont(typeface, 30);
        using var bodyFont = new SKFont(typeface, 26);
        using var captionFont = new SKFont(typeface, 22);
        var headingLines = WrapMeasured(identity?.WizardAlias ?? snapshot.OfficialName, heading, 1060).ToArray();
        var titleLines = WrapMeasured(identity is null ? edition.Story.Title : snapshot.OfficialName + " · " + edition.Story.Title, titleFont, 1060).ToArray();
        var summary = edition.Story.Text.Length <= 600 ? edition.Story.Text : edition.Story.Text[..600] + "…";
        var bodyLines = identity is null ? WrapMeasured(summary, bodyFont, 1060).ToArray()
            : IdentityText(identity).Split('\n').SelectMany(line => WrapMeasured(line, bodyFont, 1060).Append("")).ToArray();
        var artworkHeight = artwork is null ? 80 : 1070;
        var height = 110 + headingLines.Length * 52 + titleLines.Length * 40 + artworkHeight
            + bodyLines.Length * 36 + (portrait is null ? 0 : 800) + 180;
        using var bitmap = new SKBitmap(1200, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColor.Parse(CardMidnight));
        using var ink = new SKPaint { Color = SKColor.Parse(CardMist), IsAntialias = true };
        using var accentInk = new SKPaint { Color = SKColor.Parse(CardPeriwinkle), IsAntialias = true };
        using var captionInk = new SKPaint { Color = SKColor.Parse(CardSteel), IsAntialias = true };
        using var border = new SKPaint { Color = SKColor.Parse(CardSteel), Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
        canvas.DrawRect(new SKRect(30, 30, 1170, height - 30), border);
        var y = 110f;
        foreach (var line in headingLines)
        {
            canvas.DrawText(line, 70, y, SKTextAlign.Left, heading, ink); y += 52;
        }
        foreach (var line in titleLines)
        {
            canvas.DrawText(line, 70, y, SKTextAlign.Left, titleFont, accentInk); y += 40;
        }
        if (artwork is not null)
        {
            using var wand = SKBitmap.Decode(artwork);
            canvas.DrawBitmap(wand, new SKRect(270, y + 20, 930, y + 1010), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        }
        else
        {
            canvas.DrawText("Digitální obraz ještě není připravený.", 70, y + 40, SKTextAlign.Left, bodyFont, captionInk);
        }
        y += artworkHeight;
        foreach (var line in bodyLines)
        {
            canvas.DrawText(line, 70, y, SKTextAlign.Left, bodyFont, ink); y += 36;
        }
        if (portrait is not null)
        {
            using var photo = SKBitmap.Decode(portrait);
            canvas.DrawBitmap(photo, new SKRect(230, y + 30, 970, y + 770), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            y += 800;
        }
        canvas.DrawText("Digitální návrh hůlky. Smyšlený příběh; celý text je v story.txt.", 70, height - 100, SKTextAlign.Left, captionFont, captionInk);
        canvas.DrawText(edition.Synthetic ? "SYNTHETICKÝ TEST — BEZ OPENAI" : "Autorská digitální edice vytvořená pomocí OpenAI.",
            70, height - 65, SKTextAlign.Left, captionFont, captionInk);
        canvas.Flush();
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    internal static IEnumerable<string> WrapMeasured(string text, SKFont font, float width)
    {
        var line = "";
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (font.MeasureText(word) > width)
            {
                if (line.Length > 0) { yield return line; line = ""; }
                foreach (var rune in word.EnumerateRunes())
                {
                    var proposedRune = line + rune;
                    if (font.MeasureText(proposedRune) > width && line.Length > 0)
                    {
                        yield return line;
                        line = rune.ToString();
                    }
                    else { line = proposedRune; }
                }
                continue;
            }
            var proposed = line.Length == 0 ? word : line + " " + word;
            if (font.MeasureText(proposed) > width && line.Length > 0)
            {
                yield return line;
                line = word;
            }
            else { line = proposed; }
        }
        if (line.Length > 0) { yield return line; }
    }

    private static string StoryText(KeepsakeIssueSnapshot snapshot, KeepsakeEdition edition, WizardIdentityCard? identity = null) =>
        (identity is null ? "" : IdentityText(identity) + "\n\n") + snapshot.OfficialName + "\n" + edition.Story.Title + "\n\n" + edition.Story.Text + "\n\n"
        + "Katalogová fakta: " + snapshot.CatalogFacts + "\n"
        + "Digitální návrh, nikoli fotografie ani přesná replika fyzické hůlky. Příběh je smyšlený.\n"
        + (edition.Synthetic ? "SYNTHETICKÝ TEST — bez OpenAI inference.\n" : "Autorská digitální edice vytvořená pomocí OpenAI.\n");

    private static string IdentityText(WizardIdentityCard identity) =>
        identity.WizardAlias + "\n" + identity.HouseOrGuild + "\n"
        + "Schopnost: " + identity.SignatureAbility + "\nSlabina: " + identity.Weakness + "\n"
        + "Specializace: " + identity.DubiousSpecialization + "\n" + identity.MinistryAssessment + "\n"
        + identity.WandBond + "\nMotto: " + identity.Motto + "\n" + identity.MicroStory + "\n"
        + "Veřejná indicie: " + identity.Omen + "\nKomediální fikce. " + identity.ContentVersion;

    // The exported layout is local and accessible; model output never contains SVG or executable markup.
    internal static string BuildCard(KeepsakeIssueSnapshot snapshot, KeepsakeEdition edition,
        string imageMime, byte[] artwork, byte[]? portrait)
    {
        var lines = Wrap(edition.Story.Text, 66).ToArray();
        var height = 1120 + lines.Length * 30 + (portrait is null ? 0 : 540);
        var svg = new StringBuilder();
        svg.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"900\" height=\"{height}\" viewBox=\"0 0 900 {height}\" role=\"img\" aria-labelledby=\"title description\">");
        svg.Append("<title id=\"title\">").Append(Escape(snapshot.OfficialName)).Append(" — digitální památka</title>");
        svg.Append("<desc id=\"description\">").Append(Escape(StoryText(snapshot, edition))).Append("</desc>");
        svg.Append($"<rect width=\"900\" height=\"100%\" fill=\"{CardMidnight}\"/><rect x=\"24\" y=\"24\" width=\"852\" height=\"{height - 48}\" fill=\"none\" stroke=\"{CardSteel}\" stroke-width=\"2\"/>");
        svg.Append($"<g fill=\"{CardMist}\" font-family=\"Inter,Arial,sans-serif\">");
        svg.Append("<text x=\"60\" y=\"85\" font-size=\"30\">").Append(Escape(snapshot.OfficialName)).Append("</text>");
        svg.Append($"<text x=\"60\" y=\"128\" font-size=\"23\" fill=\"{CardPeriwinkle}\">").Append(Escape(edition.Story.Title)).Append("</text>");
        svg.Append("<image x=\"210\" y=\"165\" width=\"480\" height=\"720\" preserveAspectRatio=\"xMidYMid meet\" href=\"data:")
            .Append(Escape(imageMime)).Append(";base64,").Append(Convert.ToBase64String(artwork)).Append("\"/>");
        var y = 940;
        foreach (var line in lines)
        {
            svg.Append($"<text x=\"60\" y=\"{y}\" font-size=\"21\">").Append(Escape(line)).Append("</text>");
            y += 30;
        }
        if (portrait is not null)
        {
            svg.Append($"<image x=\"200\" y=\"{y + 25}\" width=\"500\" height=\"500\" href=\"data:image/png;base64,")
                .Append(Convert.ToBase64String(portrait)).Append("\"/>");
            y += 540;
        }
        svg.Append($"<text x=\"60\" y=\"{y + 48}\" font-size=\"17\" fill=\"{CardSteel}\">Digitální návrh; smyšlený příběh. Nejde o fotografii fyzické hůlky.</text>");
        if (edition.Synthetic) { svg.Append($"<text x=\"60\" y=\"{y + 80}\" font-size=\"17\" fill=\"{CardSteel}\">SYNTHETICKÝ TEST — BEZ OPENAI</text>"); }
        return svg.Append("</g></svg>").ToString();
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? "";
    private static IEnumerable<string> Wrap(string text, int limit)
    {
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            var remaining = paragraph;
            while (remaining.Length > limit)
            {
                var end = remaining.LastIndexOf(' ', limit);
                if (end <= 0) { end = limit; }
                yield return remaining[..end];
                remaining = remaining[end..].TrimStart();
            }
            yield return remaining;
        }
    }
}
