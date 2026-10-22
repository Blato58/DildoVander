using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Kufr.Backend.Features.Conversations;

internal sealed record PersonalityInfo(string Id, string Name, string Description, string PromptSha256);

internal static class PersonalityCatalog
{
    internal const string DefaultId = "party-original";
    internal const string OllivanderId = "ollivander-trunk";
    private static readonly string Ollivander = PartyDialoguePrompt.ReadResource("Kufr.Backend.OllivanderPersona", trim: false);
    private static readonly IReadOnlyList<PersonalityInfo> inventory = Array.AsReadOnly(new[]
    {
        Info(DefaultId, "Stínový čaroděj", "Původní divadelní hlas kufru se suchým humorem; obvykle 4–7 otázek.", PartyDialoguePrompt.VoicePersona),
        Info(OllivanderId, "Mistr hůlek", "Původní komediální hlas pojízdné dílny; obvykle 2 odpovědi a výběr hůlky.", Ollivander)
    });

    internal static IReadOnlyList<PersonalityInfo> Inventory => inventory;
    internal static bool Contains(string id) => inventory.Any(item => item.Id == id);
    internal static string Persona(string id) => id switch
    {
        DefaultId => PartyDialoguePrompt.VoicePersona,
        OllivanderId => Ollivander,
        _ => throw new ArgumentException("Unknown personality.", nameof(id))
    };
    internal static string DecisionInstructions(string id) => Persona(id) + "\n\n" + PartyDialoguePrompt.SelectionInstructions;

    internal static string AutonomousLivePersona(string id) => id switch
    {
        OllivanderId => PartyDialoguePrompt.ReadResource("Kufr.Backend.OllivanderLivePersona"),
        DefaultId => PartyDialoguePrompt.VoicePersona.Replace(
            "Typicky 4–7 otázek včetně úvodní. Serverová brána určuje, kdy je výběr povolený. Po splnění brány zakonči, jakmile máš materiál; nevynucuj sedm kol.",
            "Důvěryhodná provozní politika určuje rytmus; vlastní otázky a roast tvoříš sám. Po splnění politiky směřuj k ověřenému výběru.", StringComparison.Ordinal)
            + "\nDelegation policy:\nBackend tools: Výběr skutečné hůlky, dostupnost, povolená paměť a platnost operace. "
            + "Delegate to the backend when: Je čas vybrat; potřebuješ neznámou dostupnost nebo vzpomínku; host opravuje výběr, "
            + "žádá alternativu či změnu nebo zrušení operace. Do not delegate to the backend when: Pozdrav, roast, běžná "
            + "reakce, vlastní navazující otázka, rozvíjení odpovědi, poděkování nebo zopakování známé informace. "
            + "Backend neposkytuje hotovou repliku. Ověřený výsledek oznam vlastními slovy; žádný výběr ani fyzickou akci si nevymýšlej.",
        _ => throw new ArgumentException("Unknown personality.", nameof(id))
    };

    // The new character permits early selection on a useful first answer or
    // an explicit request. The prompt sets the usual two-answer cadence.
    internal static int MinimumQuestions(string id) => id == OllivanderId ? 1 : ResponsesTextDialogBudget.MinimumQuestions;
    internal static int FakeQuestions(string id) => id == OllivanderId ? 2 : ResponsesTextDialogBudget.MinimumQuestions;

    internal static string OpeningInstructions(string id, string variation) => id == DefaultId
        ? ConversationOpening.Instructions(variation)
        : "NOVÝ ROZHOVOR: zákazník ještě nic neřekl. Začni podle zvolené personality krátkým přivítáním, "
          + "rýpnutím a jednou otázkou. Nevymýšlej jeho odpověď ani totožnost. "
          + "Interní identifikátor úvodu: " + variation + ". Nevyslovuj jej.";

    internal static int ExtraRequestBytes(string id)
    {
        var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        return Math.Max(0, Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(DecisionInstructions(id), options))
            - Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(PartyDialoguePrompt.DecisionInstructions, options)));
    }

    private static PersonalityInfo Info(string id, string name, string description, string prompt) =>
        new(id, name, description, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))));
}
