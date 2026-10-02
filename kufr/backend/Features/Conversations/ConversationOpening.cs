using System.Security.Cryptography;

namespace Kufr.Backend.Features.Conversations;

// Rotate intentions, not canned lines. Allocate once per generated opening;
// Realtime retains the result while calculating reservations/configuring audio.
internal static class ConversationOpening
{
    private static readonly string[] Directions =
    [
        "Zeptej se na jméno nebo přezdívku. Bez další osobní otázky.",
        "Otevři snadnou otázkou na drobnou věc, kterou host odkládá.",
        "Otevři otázkou na neškodný zlozvyk, který si host sám omlouvá.",
        "Zeptej se na malou blbost, kterou hostovi připomínají kamarádi.",
        "Nabídni hravou volbu: host raději plánuje, nebo improvizuje. Jedna otázka.",
        "Zeptej se na maličkost, ve které si host věří víc, než by měl."
    ];
    private static int sequence = RandomNumberGenerator.GetInt32(Directions.Length);

    internal static string Next() => Directions[(int)((uint)Interlocked.Increment(ref sequence) % Directions.Length)];

    internal static string Instructions(string direction) =>
        "NOVÝ ROZHOVOR: příchozí ještě nic neřekl a jeho totožnost neznáš. "
        + "Začni krátkým přirozeným oslovením a právě jednou otázkou podle tohoto námětu: "
        + direction + " Formuluj ji vlastními slovy. Nevymýšlej společný zážitek ani hostovu odpověď. "
        + "Neodpovídej na archiv, příklady ani technická pravidla a nemluv o datech nebo paměti.";
}
