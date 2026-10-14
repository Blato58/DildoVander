using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Kufr.Backend.Features.Conversations;

namespace Kufr.Backend.Features.GptLive;

internal static class LiveTerminalSpeech
{
    internal static bool MentionsAnyCatalogTitle(string spoken, IReadOnlyList<PartyWandCatalog.WandEntry> catalog)
    {
        var words = Words(spoken);
        return catalog.Any(wand => Mentions(words, wand.DisplayName));
    }
    internal static bool MentionsCompartmentIdentifier(string spoken)
    {
        // Czech speech commonly spells numbers and declines ordinals. A limited
        // finale must not publish any numbered compartment, even an invalid one.
        const string compartment = @"(?:slot(?:u|em|y|ech|um|a)?|prihrad(?:ka|ku|ce|ky|ek|kam|kach|kami|kou))";
        const string number = @"(?:\d+|jeden|jedn(?:a|u|e|o|y|ou)|dva|dve|dvou|dvema|tri|trech|tremi|ctyri|ctyrech|ctyrmi|pet|peti|pati|sest|sesti|sedm|sedmi|osm|osmi|devet|deviti|deset|deseti|(?:prvn|tret)(?:i|iho|imu|im|ich|imi)|(?:druh|ctvrt|pat|sest|sedm|osm|devat|desat)(?:y|a|e|ou|eho|emu|ych|ym|ymi|em)|(?:jednick|dvojk|trojk|ctyrk|petk|sestk|sedmick)(?:a|u|y|ou|e))";
        const string gap = @"\s+(?:\p{L}+\s+){0,2}";
        var tokens = string.Join(" ", Regex.Matches(Plain(spoken), @"\p{L}+|\d+",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20)).Select(match => match.Value));
        return Regex.IsMatch(tokens, $@"\b(?:{compartment}{gap}{number}|{number}{gap}{compartment})\b",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20));
    }
    private static readonly HashSet<string> CaseEndings = new(StringComparer.Ordinal)
        { "", "a", "e", "i", "o", "u", "y", "ou", "em", "im", "ym", "ho", "mu", "eho", "emu", "ovi", "ove", "ich", "ych", "ech", "um", "ami", "emi", "imi", "ymi" };

    internal static bool Ready(string spoken, int? compartment, IReadOnlyList<PartyWandCatalog.WandEntry> catalog)
    {
        // Live fragments need not contain a final full stop. An explicitly
        // unfinished fragment still blocks the checkpoint, including a dramatic
        // pause immediately after a name followed by a dash.
        if (Unfinished(spoken)) return false;
        if (compartment is null) return true;
        var words = Words(spoken);
        var mentioned = catalog.Where(w => Mentions(words, w.DisplayName)).Select(w => w.Compartment).ToArray();
        return mentioned.Length == 1 && mentioned[0] == compartment;
    }

    internal static bool Unfinished(string spoken)
    {
        var text = spoken.TrimEnd().TrimEnd('"', '„', '“', '”', '\'').TrimEnd();
        return text.Length == 0 || text.EndsWith("...", StringComparison.Ordinal) || ",;:–—-…".Contains(text[^1]);
    }

    private static bool Mentions(string[] spoken, string name)
    {
        // The distinctive catalog title identifies the same physical item when
        // Live omits the wood prefix or declines its Czech name. Never match
        // descriptions, visitor facts, or a name invented by the model.
        var marker = name.IndexOf(" hůlka ", StringComparison.OrdinalIgnoreCase);
        var title = Words(marker < 0 ? name : name[(marker + " hůlka ".Length)..]);
        for (var start = 0; start + title.Length <= spoken.Length; start++)
        {
            if (title.Select((word, offset) => SameWord(word, spoken[start + offset])).All(match => match)) return true;
        }
        return false;
    }

    private static bool SameWord(string expected, string actual)
    {
        if (expected == actual) return true;
        if (expected.Length < 5) return false;
        var stem = "aeiouy".Contains(expected[^1]) ? expected[..^1] : expected;
        return actual.StartsWith(stem, StringComparison.Ordinal) && CaseEndings.Contains(actual[stem.Length..]);
    }

    private static string[] Words(string text)
    {
        return Regex.Matches(Plain(text), @"\p{L}+").Select(m => m.Value).ToArray();
    }
    private static string Plain(string text) => string.Concat(text.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)).ToLowerInvariant();
}
