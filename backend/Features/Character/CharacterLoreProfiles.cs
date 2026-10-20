using Kufr.Backend.Features.Conversations;

namespace Kufr.Backend.Features.Character;

internal sealed record CharacterLoreProfile(
    int Compartment,
    string CatalogVersion,
    string CatalogDisplayName,
    string Premise,
    IReadOnlyList<string> LanguageMotifs,
    string AdultExample,
    string ChaosExample,
    string SuitableCallback,
    string UnsuitableCallback,
    string FactualBoundary,
    SceneIntent? SceneIntent = null);

// Authorial fiction only. No selection weights, new catalog facts or inferred guest traits.
internal static class CharacterLoreProfiles
{
    internal const string Version = "dv01-lore-v1";
    private static readonly IReadOnlyList<CharacterLoreProfile> Profiles = ValidateCatalog();

    internal static IReadOnlyList<CharacterLoreProfile> All => Profiles;

    internal static CharacterLoreProfile? ForConfirmedSelection(int compartment, string catalogVersion) =>
        Profiles.SingleOrDefault(profile => profile.Compartment == compartment && profile.CatalogVersion == catalogVersion);

    private static IReadOnlyList<CharacterLoreProfile> ValidateCatalog()
    {
        var profiles = new[]
        {
            Profile(1, "Jasanová hůlka Poslední slovo", "Tichý puntičkář, který chce neopravitelný závěr.",
                ["příloha", "oprava posudku", "poslední slovo"],
                "Poslední slovo bude mít ona. Ty můžeš dodat přílohy, ať ten zkurvenej posudek aspoň drží pohromadě.",
                "Tvoje vysvětlování zařadila mezi přírodní katastrofy. Poslední slovo má ona; ty drž hubu a archivuj přílohy.",
                "Host výslovně popsal přesnost nebo dlouhé vysvětlování.", "Nevydávat mlčení za introverzi.",
                "Jasan a jantarová rukojeť pocházejí z katalogu; puntičkářství je fikce."),
            Profile(2, "Třešňová hůlka Parketový kompas", "Sebevědomý performer, který neumí nenápadný vstup.",
                ["velkolepý návrat", "parket", "nenápadnost"],
                "Ztratit se můžeš. Nenápadně rozhodně ne; tahle dělá z každého návratu kurva slavnostní průvod.",
                "Nenápadný vstup? Hovno. Tahle zahájí průvod i při návratu ze záchodu a ještě si nechá zaplatit vstupný.",
                "Výslovně chtěné dobrodružství, tanec nebo teatrální vstup.", "Nehádat společenskost z fotografie.",
                "Třešeň a měděná spirála jsou katalogové; průvod je komické přirovnání."),
            Profile(3, "Ořechová hůlka Výbor pro špatné nápady", "Byrokratický vynálezce, který katastrofu řádně schvaluje.",
                ["jednomyslně", "razítko", "zamítnutý návrh"],
                "Zdravý rozum návrh zamítl. Výbor ho schválil jednomyslně; kurva, aspoň někde funguje administrativa.",
                "Výbor schválil tvoji katastrofu jednomyslně. Zdravý rozum dostal padáka a razítko jsme hodili do prdele.",
                "Hostem uvedené experimentování nebo absurdní plán.", "Nevytvářet skutečné politické přesvědčení hosta.",
                "Ořech a stříbrný uzel jsou katalogové; výbor nemá skutečnou provozní autoritu."),
            Profile(4, "Březová hůlka Sametové alibi", "Elegantní stratég, jehož improvizace vypadá jako plán.",
                ["alibi", "nevinný výraz", "promyšlený ústup"],
                "Vypadá nevinně. To je její nejpropracovanější vlastnost; na tvůj chaos bude kurva potřeba kvalitní alibi.",
                "Katastrofu obalí sametem a nazve strategií. Zkurvenej plán nemá, ale před komisí bude mít nejlepší alibi.",
                "Výslovná preference elegance nebo improvizace.", "Neodvozovat vinu, profesi ani povahu z oblečení.",
                "Bříza a matně fialový hrot jsou katalogové; alibi není tvrzení o trestném činu."),
            Profile(5, "Kaštanová hůlka Kocovina věštců", "Unavený prorok tuší následky a stejně se účastní.",
                ["ráno", "následky", "odložené proroctví"],
                "Následky tuší. Vyjadřovat se bude až ráno; na dnešní nápady už kurva nemá pracovní dobu.",
                "Budoucnost nevěští, jen reklamace odkládá na ráno. Zítřek bude v prdeli, ale dnešní směnu jí nikdo neproplatí.",
                "Hostem deklarovaná noční aktivita nebo ranní plán.", "Netvrdit, že host pije, je opilý nebo má závislost.",
                "Kaštan a mosazná hvězda jsou katalogové; nevěští skutečnou budoucnost."),
            Profile(6, "Jilmová hůlka Knihovní bouře", "Tichý badatel s nepřiměřeně velkým nápadem.",
                ["učebnice", "poznámka pod čarou", "knihovní bouře"],
                "Dlouho mlčí. Pak je nutné přepsat několik učebnic; kurva, i poznámka pod čarou může udělat škodu.",
                "Vypadá jako poznámka pod čarou. Pak odpálí celou bibliografii do prdele a knihovník musí sepsat krizový plán.",
                "Hostem vyslovená zvídavost, fantasy nebo sběratelství.", "Nevydávat krátkou odpověď za introverzi či inteligenci.",
                "Jilm a inkoustově modré žilkování jsou katalogové; bouře a škoda jsou fikce.")
        };
        var catalog = PartyWandCatalog.Available(0b111111);
        if (catalog.Count != profiles.Length || profiles.Any(profile => !catalog.Any(wand =>
            wand.Compartment == profile.Compartment && wand.DisplayName == profile.CatalogDisplayName)))
        {
            throw new InvalidOperationException("DV01 lore references do not match the current six-wand catalog.");
        }
        return Array.AsReadOnly(profiles);
    }

    private static CharacterLoreProfile Profile(int compartment, string displayName, string premise,
        string[] languageMotifs, string adult, string chaos, string suitable, string unsuitable, string boundary) =>
        new(compartment, PartyWandCatalog.Version, displayName, premise, Array.AsReadOnly(languageMotifs),
            adult, chaos, suitable, unsuitable, boundary);
}
