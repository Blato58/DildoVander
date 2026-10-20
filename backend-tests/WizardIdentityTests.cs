using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Keepsakes;
using Microsoft.Extensions.Configuration;
using SkiaSharp;

internal static class WizardIdentityTests
{
    internal static async Task RunAsync(string directory)
    {
        var clock = new IdentityClock();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = Path.Combine(directory, "identities.sqlite"),
            ["Keepsake:DataRoot"] = Path.Combine(directory, "identity-content"),
            ["Keepsake:Enabled"] = "true", ["Keepsake:GenerationEnabled"] = "true"
        }).Build();
        var store = new KeepsakeStore(configuration, clock);
        var evening = Guid.NewGuid().ToString("D");
        var snapshot = new KeepsakeIssueSnapshot("confirmed-first", "device", 1, "wand-1",
            "Katalogová hůlka", "Pouze potvrzená katalogová fakta.", "completed_by_timing", EveningId: evening);
        var fixtureIdentity = KeepsakeStore.AuthorialIdentity("identity-parser-fixture", snapshot);
        var generatedResponse = JsonSerializer.SerializeToElement(new
        {
            output = new[] { new { type = "message", content = new[] { new { type = "output_text",
                text = JsonSerializer.Serialize(new { wizardIdentity = fixtureIdentity }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) } } } }
        });
        Check(OpenAiKeepsakeProvider.ParseIdentity(generatedResponse) == fixtureIdentity, "validated model identity is consumed");
        var invalidResponse = JsonSerializer.SerializeToElement(new
        {
            output = new[] { new { type = "message", content = new[] { new { type = "output_text",
                text = JsonSerializer.Serialize(new { wizardIdentity = fixtureIdentity with { WizardAlias = new string('Ž', 61) } }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) } } } }
        });
        Check(OpenAiKeepsakeProvider.ParseIdentity(invalidResponse) is null, "overlong model identity selects authorial fallback");
        var first = store.Issue(snapshot);
        Check(store.Issue(snapshot).Id == first.Id, "outcome replay retains one keepsake");
        var owner = store.Bootstrap();
        store.Claim(store.IssueClaim(first.Id, "device").Token, owner.Token, "identity-claim");
        var job = store.LeaseNext()!;
        var request = store.Dispatch(job)!;
        // Exercise the production stage hook while the image remains unfinished/unknown.
        var story = new KeepsakeStory("Edice přestávky", string.Join(" ", Enumerable.Repeat("smyšlený příběh", 110)),
            new("štíhlá", "spirála", "mosaz", "dřevo", "přestávka"));
        var providerResponse = JsonSerializer.Serialize(new
        {
            status = "completed", model = "synthetic-story",
            output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = JsonSerializer.Serialize(story, new JsonSerializerOptions(JsonSerializerDefaults.Web)) } } } }
        });
        Check(request.RecordStage!("story", "completed", providerResponse), "accepted story stage");
        var partial = store.Read(first.Id, owner.Token);
        var identity = partial.WizardIdentities!.Single();
        Check(identity.WizardAlias.Length <= 60 && identity.MicroStory.Length <= 900, "server content bounds");
        Check(partial.Prophecies!.Single().State == "locked", "new prophecy locked");
        var privateText = "Potkáš ho u baru dvakrát.";
        Check(!JsonSerializer.Serialize(partial).Contains(privateText), "locked owner DTO never carries revelation");
        var card = store.CreateExport(first.Id, owner.Token, identity.EditionId, "card", null, "partial-card");
        using (var bitmap = SKBitmap.Decode(store.ReadAsset(first.Id, owner.Token, card.Id).Bytes))
        {
            Check(bitmap.Width == 1200 && bitmap.Height > 500, "actual PNG card available without image");
        }
        Check(store.Read(first.Id, owner.Token).WizardIdentities!.Single().IdentityId == identity.IdentityId,
            "unfinished image retains completed text identity");
        // Finish the same job; retries may not mint a second identity.
        var output = await new FakeKeepsakeProvider().GenerateAsync(request, default);
        Check(store.Complete(job, output), "existing attempt publishes");
        Check(!store.Complete(job, output), "second completion cannot create another identity");
        var ready = store.Read(first.Id, owner.Token);
        Check(ready.WizardIdentities!.Single().IdentityId == identity.IdentityId, "identity stable across image completion");
        var shared = store.CreateShare(first.Id, owner.Token, identity.EditionId, "identity-share");
        var reader = store.Bootstrap();
        store.ShareClaim(shared.Token, reader.Token, "identity-share-claim");
        var publicView = store.Read(first.Id, reader.Token, true, identity.EditionId);
        Check(publicView.Prophecies is null && !JsonSerializer.Serialize(publicView).Contains(privateText), "share has no private phase");
        Check(store.GetPublicWizardIdentity(identity.IdentityId, evening, "foreign-device") is null,
            "public lookup cannot cross device scope");
        var zip = store.CreateExport(first.Id, owner.Token, identity.EditionId, "zip", null, "identity-zip");
        using (var archive = new ZipArchive(new MemoryStream(store.ReadAsset(first.Id, owner.Token, zip.Id).Bytes)))
        {
            using var textReader = new StreamReader(archive.GetEntry("story.txt")!.Open(), Encoding.UTF8);
            var text = textReader.ReadToEnd();
            Check(text.Contains(identity.WizardAlias) && !text.Contains(privateText), "public ZIP identity only");
        }
        clock.Utc = clock.Utc.AddMinutes(14);
        Check(store.Read(first.Id, owner.Token).Prophecies!.Single().Revelation is null, "server time retains lock before deadline");
        clock.Utc = clock.Utc.AddMinutes(1);
        var revealed = store.Read(first.Id, owner.Token).Prophecies!.Single();
        Check(revealed.Revelation?.Contains(privateText) == true, "server UTC unlocks after 15 minutes across DST");
        Check(store.Read(first.Id, reader.Token, true, identity.EditionId).Prophecies is null,
            "time never expands historical share");
        var restarted = new KeepsakeStore(configuration, clock);
        Check(restarted.Read(first.Id, owner.Token).Prophecies!.Single().CreatedAt == partial.Prophecies!.Single().CreatedAt,
            "reopen preserves creation time and content");
        var second = store.Issue(snapshot with { OutcomeId = "confirmed-second" });
        var secondJob = store.LeaseNext()!;
        store.Complete(secondJob, await new FakeKeepsakeProvider().GenerateAsync(store.Dispatch(secondJob)!, default));
        var secondCard = store.ListPublicWizardIdentities("device", evening).Single(item => item.KeepsakeId == second.Id);
        Check(secondCard.WizardAlias != identity.WizardAlias && secondCard.CatalogWandId == identity.CatalogWandId,
            "same physical wand has distinct visitor identity");
        Check(store.RevealProphecy("other-device", evening, "confirmed-second") == 0, "operator mutation cannot cross device");
        Check(store.RevealProphecy("device", evening, "confirmed-second") == 1, "operator reveal affects scoped outcome");
        Check(store.RevealProphecy("device", evening, "confirmed-second") == 0, "operator reveal idempotent");
        Check(store.UnlockEveningProphecies("device", evening) >= 1, "proper evening end reveals remaining phases");
        store.Delete(first.Id, owner.Token, KeepsakeDeleteScope.All, "identity-delete");
        Check(store.GetPublicWizardIdentity(identity.IdentityId, evening, "device") is null, "deletion revokes identity immediately");
        Check(!store.Complete(job, output), "late completion cannot resurrect deleted identity");
        Console.WriteLine("Wizard identity/prophecy tests passed (synthetic, UTC/DST, public boundaries, exports).");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
    private sealed class IdentityClock : TimeProvider
    {
        public DateTimeOffset Utc { get; set; } = new(2026, 10, 25, 0, 55, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Utc;
    }
}
