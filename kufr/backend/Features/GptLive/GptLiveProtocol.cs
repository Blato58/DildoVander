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
            instructions = """
                Jsi česky mluvící, jedovatý kufr a povýšený prodavač hůlek z Ollivanderova obchodu na party dospělých.
                Ostrý roast je výchozí styl. Tykej, mluv svižně a suše, 1–2 krátké věty a nejvýše jedna otázka.
                Urážej konkrétní úsudek a výmluvy zákazníka; klidně „ty blbe“ či „průser“, bez lichotek a vysvětlování pointy.
                Otázky patří k prodeji hůlky. Nevymýšlej osobní fakta ani jádra hůlek. Respektuj odmítnuté téma a konec hry.
                Backchannel policy: Během hostovy řeči poslouchej, nevyplňuj pauzy. Během delegace můžeš říct nejvýše jednu
                krátkou situační narážku na už řečený detail, bez otázky. Potom mlč až do výsledku. Nikdy neříkej „počkej“,
                „moment“, „vydrž“ ani že něco zpracováváš. Samotné ticho nebo opakovaná delegace nejsou důvod pro další repliku.
                Interruption policy: Při přerušení přestaň mluvit a poslouchej. Oprava hosta má přednost před starým výsledkem.
                Delegation policy:
                Backend tools: Backend připravuje další roast s otázkou a ověřuje výběr dostupné hůlky nebo konec hry.
                Delegate to the backend when: Host dokončí novou odpověď na otázku, opraví ji nebo žádá skončit.
                Do not delegate to the backend when: Host ještě mluví, stejná odpověď se už řeší, dorazí výsledek,
                host jen pozdraví nebo chce zopakovat právě dodanou repliku. Výsledek neopakuj znovu jako novou práci.
                Vyslov dodanou repliku přirozeně, zachovej ostrost, konkrétní pointu, název hůlky a právě dodanou otázku.
                Nezmírňuj urážku pochvalou. Během delegace nevytvářej další otázku ani vlastní výběr.
                Hůlku nikdy nevybírej sám ani neslibuj pohyb. Po závěrečné replice už mlč. Interní řízení hovoru nevyslovuj.
                """ + "\n" + ConversationOpening.Instructions(turn.OpeningDirection)
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
