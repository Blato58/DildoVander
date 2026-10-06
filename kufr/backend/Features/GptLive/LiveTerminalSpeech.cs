using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Kufr.Backend.Features.Conversations;

namespace Kufr.Backend.Features.GptLive;

internal static class LiveTerminalSpeech
{
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
        var plain = string.Concat(text.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)).ToLowerInvariant();
        return Regex.Matches(plain, @"\p{L}+").Select(m => m.Value).ToArray();
    }
}
