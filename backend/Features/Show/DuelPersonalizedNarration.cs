using System.Text;
using System.Text.RegularExpressions;

namespace Kufr.Backend.Features.Show;

// Rendering fiction never changes the immutable event. The same fallback is
// useful offline, during provider failure and before optional commentary arrives.
internal static class DuelPersonalizedNarration
{
    internal static string Spell(DuelIdentitySnapshot wand, string action) =>
        wand.Spells?.SingleOrDefault(spell => spell.Action == action)?.Name ?? action;

    internal static string Round(DuelIdentitySnapshot left, DuelIdentitySnapshot right, DuelRound round)
    {
        var opening = round.Number switch { 1 => "Opona vzhůru.", 2 => "Důstojnost už odešla na přestávku.", _ => "Poslední kolo; ministerská komise předstírá napětí." };
        var exchange = (round.LeftAction, round.RightAction) switch
        {
            ("strike", "trick") or ("trick", "strike") => "Přímá magie proniká kouřovou clonou.",
            ("trick", "shield") or ("shield", "trick") => "Klam obchází obranu postranním vchodem pro úředníky.",
            ("shield", "strike") or ("strike", "shield") => "Zásah naráží do štítu jako stížnost do zavřené podatelny.",
            _ => "Stejná kouzla; rozhoduje jejich vlastní povaha, nikoli rozhodčího nálada."
        };
        var outcome = round.LeftScore == round.RightScore ? "Oba získávají půl bodu. Komise žádá celé razítko."
            : $"Bod získává {Bound(round.LeftScore > round.RightScore ? left.DisplayName : right.DisplayName)}. Poražená důstojnost se neproplácí.";
        return $"{opening} {Bound(left.DisplayName)} sesílá {Bound(Spell(left, round.LeftAction))}; {Bound(right.DisplayName)} odpovídá {Bound(Spell(right, round.RightAction))}. {exchange} {outcome}";
    }

    internal static string Mishap(DuelIdentitySnapshot left, DuelIdentitySnapshot right, DuelRound round) => round.Number switch
    {
        1 => $"{Bound(left.WandName ?? left.WandId)} rozsvítila neexistující kontrolku sebevědomí.",
        2 => $"{Bound(right.WandName ?? right.WandId)} si vyžádala pauzu na obdiv. Žádost byla teatrálně zamítnuta.",
        _ => "Neviditelný zapisovatel převrhl kalamář do vlastní kariéry. Body zůstávají suché."
    };

    internal static string Progression(IReadOnlyList<DuelRound> rounds)
    {
        var left = rounds.Sum(round => round.LeftScore > round.RightScore ? 1 : round.LeftScore == round.RightScore ? .5 : 0);
        var right = rounds.Count - left;
        return left == right ? "Souboj zůstává vyrovnaný." : left > right ? "Levá postava přebírá vedení." : "Pravá postava přebírá vedení.";
    }

    internal static string PhysicalLine(DuelDocument duel, string stage)
    {
        // Keep the bridge's existing 500-byte speech bound, even for names made
        // entirely of four-byte Unicode symbols. Rich web narration stays whole.
        string Short(string text) => string.Concat(text.EnumerateRunes().Take(12).Select(rune => rune.ToString()));
        var left = Short(duel.Left.DisplayName); var right = Short(duel.Right.DisplayName);
        if (stage == "intro_dispatch")
            return $"{left}, hůlka {Short(duel.Left.WandName ?? duel.Left.WandId)}, proti {right}, hůlka {Short(duel.Right.WandName ?? duel.Right.WandId)}. Digitální souboj začíná. Hůlky zůstávají v přihrádkách.";
        if (stage == "final_dispatch")
        {
            var winner = duel.Result!.Winner == "draw" ? "Remíza." : $"Vítězí {(duel.Result.Winner == "left" ? left : right)}.";
            return $"{(duel.Mode == "exhibition" ? "Fiktivní exhibice. " : "")}{winner} Body {duel.Result.LeftPoints} ku {duel.Result.RightPoints}. Komise tleská na vlastní náklady.";
        }
        var round = duel.Rounds[^1];
        var outcome = round.LeftScore == round.RightScore ? "Remíza kola." : $"Bod získává {(round.LeftScore > round.RightScore ? left : right)}.";
        return $"Kolo {round.Number}. {left}: {Short(Spell(duel.Left, round.LeftAction))}. {right}: {Short(Spell(duel.Right, round.RightAction))}. {outcome} Skóre {round.LeftScore} ku {round.RightScore}. Ministerstvo zadržuje potlesk.";
    }

    internal static bool Flavor(string? text) => text is { Length: >= 1 and <= 360 }
        && !text.Any(character => char.IsControl(character) || char.IsDigit(character) || character is '<' or '>')
        && !Regex.IsMatch(text, @"https?:|vítěz|vyhr|prohr|remíz|skóre|\b(?:winner|wins|loses|draw|score|points|servo|gpio|esp32|actuator|hardware|token|prompt)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static string Bound(string value) => new(value.Where(character => !char.IsControl(character)).Take(100).ToArray());
}
