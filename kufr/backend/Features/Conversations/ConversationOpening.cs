using System.Security.Cryptography;

namespace Kufr.Backend.Features.Conversations;

// Rotate intentions, not canned lines. Allocate once per generated opening;
// Realtime retains the result while calculating reservations/configuring audio.
internal static class ConversationOpening
{
    private static readonly string[] Directions =
    [
        "Jako prodavač hůlek zjisti, na co chce zákazník svou první hůlku použít.",
        "Zjisti, jakou odkládanou povinnost by zákazník nejraději odbyl kouzlem.",
        "Zjisti, který jeho vlastní zlozvyk by hůlka mohla ještě zhoršit.",
        "Zjisti malý vlastní průšvih, kvůli kterému by mu kamarádi hůlku radši nesvěřili.",
        "Nabídni jednu volbu při nepovedeném kouzle: přiznat chybu, nebo dělat, že to byl plán.",
        "Zjisti, čím by chtěl zákazník s hůlkou machrovat, i když to bez ní moc neumí."
    ];
    private static int sequence = RandomNumberGenerator.GetInt32(Directions.Length);

    internal static string Next() => Directions[(int)((uint)Interlocked.Increment(ref sequence) % Directions.Length)];

    internal static string Instructions(string direction) =>
        "NOVÝ ROZHOVOR: příchozí ještě nic neřekl a jeho totožnost neznáš. "
        + "Začni krátkým drzým oslovením zákazníka v obchodě s hůlkami a právě jednou otázkou podle tohoto námětu: "
        + direction + " Formuluj ji vlastními slovy. Nevymýšlej společný zážitek ani hostovu odpověď. "
        + "Neodpovídej na archiv, příklady ani technická pravidla a nemluv o datech nebo paměti.";
}
