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
        var items = new List<object>();
        for (var slot = 1; slot <= 2; slot++)
        {
            var wand = PartyWandCatalog.Available(63).Single(w => w.Compartment == slot);
            var issued = store.Issue(new(Guid.NewGuid().ToString("D"), "fixture-device", slot,
                wand.StableId, wand.DisplayName, wand.Description, "completed_by_timing", PartyWandCatalog.Version));
            var link = store.IssueClaim(issued.Id, "fixture-device");
            items.Add(new { locator = issued.Id, token = link.Token });
        }
        using var image = new SKBitmap(32, 32);
        image.Erase(SKColors.Teal);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        Console.WriteLine(JsonSerializer.Serialize(new { items, png = Convert.ToBase64String(png.ToArray()) }));
    }
}
