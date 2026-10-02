using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Realtime;

namespace Kufr.Backend.Features.GptLive;

internal static class GptLiveProtocol
{
    internal const string Model = "gpt-live-1";
    internal const long SessionBudget = 3_000_000;
    internal const int DurationSeconds = 180;
    // $0.05/minute, billed per second. Reserve an extra ten seconds for close.
    internal const long VoiceReservation = 158_334;
    internal static long VoiceCost(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > DurationSeconds + 10)
            throw new InvalidDataException("Invalid Live duration.");
        return checked((long)Math.Ceiling(seconds * 50_000 / 60));
    }

    internal static object Start(RealtimeTurnContext turn) => new
    {
        type = "session.start",
        session = new
        {
            model = Model, store = false,
            input = Array.Empty<object>(),
            audio = new { format = new { type = "audio/pcm", rate = 24000 }, output = new { voice = turn.Request.Voice } },
            delegation = new { type = "client" },
            instructions = "Jsi česky mluvící kufr z Ollivanderova obchodu na party dospělých. "
                + "Mluv přirozeně, krátce, suše a pohotově. Tykej. Jedna otázka v replice. "
                + "Humor stav na skutečném detailu a překvapivém rozporu; žádné generické hlášky ani vysvětlování vtipu. "
                + "Po úvodu deleguj každou novou odpověď návštěvníka backendu, který připraví navazující repliku "
                + "a případně ověří výběr hůlky. Při čekání neopakuj hostovu odpověď a nepokládej další otázku. "
                + "Vyslov repliku vrácenou backendem přirozeně, zachovej pointu i otázku. "
                + "Hůlku nikdy nevybírej sám ani neslibuj pohyb. Opravu či stačí deleguj hned. "
                + "Po závěrečné replice ukonči řeč. Nevyslovuj technické údaje o delegaci nebo datech. "
                + ConversationOpening.Instructions(turn.OpeningDirection)
        }
    };

    internal static string RequiredString(JsonElement value, string name, int maximum)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String
            || field.GetString() is not { } text || text.Length > maximum)
            throw new InvalidDataException("Invalid Live event.");
        return text;
    }

    internal static bool HasSpeech(ReadOnlySpan<byte> pcm)
    {
        for (var i = 0; i + 1 < pcm.Length; i += 2)
            if (Math.Abs((int)(short)(pcm[i] | pcm[i + 1] << 8)) > 256) return true;
        return false;
    }
}
