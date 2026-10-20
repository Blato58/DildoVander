using System.Text;

namespace Kufr.Backend.Features.GptLive;

// UTF-8 bytes form a conservative upper bound for byte-level BPE tokens.
// This deliberately wastes some room in Czech; character counts are not tokens.
// Reserve 32 tokens for framing and never split a Unicode scalar.
internal static class LiveTextBudget
{
    internal static int UpperBound(string text) => Encoding.UTF8.GetByteCount(text);
    internal static string Bound(string text, int tokens = 500)
    {
        var remaining = Math.Max(0, tokens - 32);
        var result = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Utf8SequenceLength > remaining) break;
            result.Append(rune); remaining -= rune.Utf8SequenceLength;
        }
        return result.ToString();
    }
}
