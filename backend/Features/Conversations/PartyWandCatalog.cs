using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kufr.Backend.Features.Conversations;

internal static class PartyWandCatalog
{
    private static readonly CatalogData Catalog = Load();
    internal static string Version => Catalog.CatalogVersion;
    internal static IReadOnlyList<WandEntry> Available(int readyMask) => Catalog.Wands
        .Where(w => (readyMask & (1 << (w.Compartment - 1))) != 0).ToArray();

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

        using var identityStream = typeof(PartyWandCatalog).Assembly
            .GetManifestResourceStream("Kufr.Backend.PartyWandIdentities")
            ?? throw new InvalidOperationException("Embedded wand identities are missing.");
        var identities = JsonSerializer.Deserialize<IdentityData>(identityStream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (identities is null || identities.CatalogVersion != catalog.CatalogVersion
            || identities.Wands is not { Length: 6 }
            || !identities.Wands.Select(w => w.Compartment).OrderBy(n => n).SequenceEqual(Enumerable.Range(1, 6))
            || identities.Wands.Any(w => string.IsNullOrWhiteSpace(w.StableId))
            || identities.Wands.Select(w => w.StableId).Distinct(StringComparer.Ordinal).Count() != 6)
        {
            throw new InvalidOperationException("Party wand identities are invalid.");
        }
        // Additive metadata lives separately so the immutable DV01 catalog and
        // its persona/provider baseline remain byte-for-byte unchanged.
        return catalog with { Wands = catalog.Wands.Select(w => w with
        { StableId = identities.Wands.Single(identity => identity.Compartment == w.Compartment).StableId }).ToArray() };
    }

    private sealed record CatalogData(string CatalogVersion, WandEntry[] Wands);
    private sealed record IdentityData(string CatalogVersion, WandIdentity[] Wands);
    private sealed record WandIdentity(int Compartment, string StableId);

    internal sealed record WandEntry(
        int Compartment,
        string DisplayName,
        string Description,
        string[] Traits, [property: JsonIgnore] string StableId = "");
}
