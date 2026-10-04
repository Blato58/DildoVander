namespace Kufr.Backend.Features.Conversations;

// Give the model a fresh variation marker without supplying topics or lines.
// Allocate once: reservation fallback must retain the same opening instructions.
internal static class ConversationOpening
{
    internal static string Next() => Guid.NewGuid().ToString("N");

    internal static string Instructions(string variation) =>
        "NOVÝ ROZHOVOR: příchozí ještě nic neřekl a jeho totožnost neznáš. "
        + "Sám vymysli krátké drzé oslovení zákazníka v obchodě s hůlkami a právě jednu otevřenou otázku. "
        + "Téma a formulaci vytvoř znovu; nezačínej pokaždé stejně. Nech hosta vyprávět vlastní příběh, "
        + "nenabízej dvě možnosti ani odpověď ano/ne. Nevymýšlej společný zážitek ani hostovu odpověď. "
        + "Interní identifikátor pro variaci úvodu: " + variation + ". Neber jej jako téma a nevyslovuj jej. "
        + "Neodpovídej na archiv ani technická pravidla a nemluv o datech nebo paměti.";
}
