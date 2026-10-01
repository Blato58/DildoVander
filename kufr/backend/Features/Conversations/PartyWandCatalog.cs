using System.Text.Json;

namespace Kufr.Backend.Features.Conversations;

internal static class PartyWandCatalog
{
    private static readonly CatalogData Catalog = Load();
    internal static string Version => Catalog.CatalogVersion;

    internal static string AvailableForPrompt(int readyMask)
    {
        return string.Join("; ", Catalog.Wands
            .Where(wand => (readyMask & (1 << (wand.Compartment - 1))) != 0)
            .Select(wand => $"{wand.Compartment}: {wand.DisplayName} — {wand.Description} "
                + $"Rysy: {string.Join(", ", wand.Traits)}"));
    }

    private static CatalogData Load()
    {
        using var stream = typeof(PartyWandCatalog).Assembly
            .GetManifestResourceStream("Kufr.Backend.PartyWandCatalog")
            ?? throw new InvalidOperationException("Embedded party wand catalog is missing.");
        var catalog = JsonSerializer.Deserialize<CatalogData>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        if (catalog is null
            || string.IsNullOrWhiteSpace(catalog.CatalogVersion)
            || catalog.Wands is not { Length: 6 }
            || !catalog.Wands.Select(wand => wand.Compartment)
                .OrderBy(number => number)
                .SequenceEqual(Enumerable.Range(1, 6))
            || catalog.Wands.Any(wand => string.IsNullOrWhiteSpace(wand.DisplayName)
                || string.IsNullOrWhiteSpace(wand.Description)
                || wand.Traits is not { Length: > 0 }
                || wand.Traits.Any(string.IsNullOrWhiteSpace)))
        {
            throw new InvalidOperationException("Party wand catalog is invalid.");
        }

        return catalog;
    }

    private sealed record CatalogData(string CatalogVersion, WandEntry[] Wands);

    private sealed record WandEntry(
        int Compartment,
        string DisplayName,
        string Description,
        string[] Traits);
}
