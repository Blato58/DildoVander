using System.Text.Json;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.Conversations;
using Microsoft.Extensions.Configuration;
using SkiaSharp;

internal static class KeepsakeHttpFixture
{
    public static void Run()
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        using var journal = new OperationJournal(configuration, TimeProvider.System);
        var store = new KeepsakeStore(configuration, TimeProvider.System);
        var evenings = new EveningMemoryStore(configuration, journal, TimeProvider.System);
        var eveningId = evenings.GetStatus("fixture-device").EveningId;
        if (configuration["KUFR_KEEPSAKE_FIXTURE_ACTION"] == "pending-cleanup")
        {
            SeedPendingCleanup(store);
            return;
        }
        var items = new List<object>();
        for (var slot = 1; slot <= 2; slot++)
        {
            var wand = PartyWandCatalog.Available(63).Single(w => w.Compartment == slot);
            var issued = store.Issue(new(Guid.NewGuid().ToString("D"), "fixture-device", slot,
                wand.StableId, wand.DisplayName, wand.Description, "completed_by_timing", PartyWandCatalog.Version, eveningId));
            var link = store.IssueClaim(issued.Id, "fixture-device");
            items.Add(new { locator = issued.Id, token = link.Token });
        }
        using var image = new SKBitmap(32, 32);
        image.Erase(SKColors.Teal);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        Console.WriteLine(JsonSerializer.Serialize(new { items, eveningId, png = Convert.ToBase64String(png.ToArray()) }));
    }

    private static void SeedPendingCleanup(KeepsakeStore store)
    {
        var wand = PartyWandCatalog.Available(63).First();
        KeepsakeIssueSnapshot Snapshot() => new(Guid.NewGuid().ToString("D"), "fixture-device", wand.Compartment,
            wand.StableId, wand.DisplayName, wand.Description, "completed_by_timing", PartyWandCatalog.Version);
        var pending = store.Issue(Snapshot());
        var job = store.LeaseNext() ?? throw new InvalidOperationException("Expected synthetic cleanup fixture job.");
        if (job.KeepsakeId != pending.Id) { throw new InvalidOperationException("Unexpected active fixture queue."); }
        var provider = new FakeKeepsakeProvider();
        var output = provider.GenerateAsync(store.Dispatch(job)!, default).GetAwaiter().GetResult();
        store.Complete(job, output);
        var preview = store.PreviewAdminDeletion([pending.Id], KeepsakeDeleteScope.All);
        var confirmationKey = Guid.NewGuid().ToString("D");
        store.BeforeAdminCleanup = () => throw new InvalidOperationException("fixture_interrupted_after_confirmation");
        try
        {
            store.ConfirmAdminDeletion(preview.Id, "delete", confirmationKey, preview.Digest);
            throw new InvalidOperationException("Expected fixture confirmation interruption.");
        }
        catch (InvalidOperationException exception) when (exception.Message == "fixture_interrupted_after_confirmation") { }
        var retained = store.Issue(Snapshot());
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            pendingId = pending.Id, retainedId = retained.Id, previewId = preview.Id,
            previewDigest = preview.Digest, confirmationKey
        }));
    }
}
