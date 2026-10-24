using Kufr.Backend.Features.Show;
using Kufr.Backend.Features.Wands;

internal static class DuelBalanceTests
{
    internal static void Run()
    {
        var profiles = WandCombatProfiles.Archetypes.Select(archetype => DuelRules.CharacterSnapshot("identity-" + archetype,
            archetype, new GeneratedWandPublicSnapshot("wand-" + archetype, 1, "Hůlka " + archetype,
                "tis", "jiskra", "vlastní povaha", "lore", "podoba", "světlo", "hluk",
                [new("strike", "První plamen", "útok"), new("trick", "Druhá ozvěna", "klam"), new("shield", "Třetí závoj", "obrana")], archetype))).ToArray();
        Check(profiles.Select(wand => (wand.Strength, wand.Cunning, wand.Defense)).Distinct().Count() == 4,
            "each archetype has its own power distribution");
        Check(profiles.All(wand => wand.Strength + wand.Cunning + wand.Defense == 9
            && new[] { wand.Strength, wand.Cunning, wand.Defense }.All(stat => stat is >= 2 and <= 4)), "equal bounded power budget");
        var comparisons = 0;
        foreach (var left in profiles)
        foreach (var right in profiles)
        {
            var payoff = new double[3, 3];
            for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
            {
                var a = DuelRules.Actions[i]; var b = DuelRules.Actions[j];
                var round = Resolve(left, right, a, b, "first");
                var otherSeed = Resolve(left, right, a, b, "different-seed");
                var mirror = Resolve(right, left, b, a, "first");
                Check(round.LeftScore == mirror.RightScore && round.RightScore == mirror.LeftScore, "all profiles/actions are side symmetric");
                Check(round.LeftScore == otherSeed.LeftScore && round.RightScore == otherSeed.RightScore, "randomness cannot overpower chosen strategy");
                payoff[i, j] = Math.Sign(round.LeftScore - round.RightScore);
                if (a != b) Check(payoff[i, j] == (((i + 1) % 3 == j) ? 1 : -1), "counter action beats every opposing stat profile");
                comparisons++;
            }
            // Exhaustive mixed-strategy grid (1% resolution): every profile pair
            // admits a strategy with fair worst-case value, and none can force
            // a winning advantage. This catches both dominant actions and an
            // archetype that would win merely by always choosing its specialty.
            var lower = -1d; var upper = 1d;
            for (var first = 0; first <= 100; first++)
            for (var second = 0; second <= 100 - first; second++)
            {
                double[] mix = [first / 100d, second / 100d, (100 - first - second) / 100d];
                lower = Math.Max(lower, Enumerable.Range(0, 3).Min(col => Enumerable.Range(0, 3).Sum(row => mix[row] * payoff[row, col])));
                upper = Math.Min(upper, Enumerable.Range(0, 3).Max(row => Enumerable.Range(0, 3).Sum(col => payoff[row, col] * mix[col])));
            }
            Check(lower is >= -.011 and <= .011 && upper is >= -.011 and <= .011, "no forced dominant strategy across all archetype pairs");
            // Simulate all 27x27 three-round action sequences for each pair.
            var leftPoints = 0d; var rightPoints = 0d; var battles = 0;
            for (var l = 0; l < 27; l++)
            for (var r = 0; r < 27; r++)
            {
                var rounds = Enumerable.Range(0, 3).Select(n => Resolve(left, right,
                    DuelRules.Actions[l / (int)Math.Pow(3, n) % 3], DuelRules.Actions[r / (int)Math.Pow(3, n) % 3], "sim", n + 1)).ToArray();
                var result = DuelRules.Result(rounds, false);
                Check(result.LeftPoints + result.RightPoints == 3, "three-round points conserved");
                leftPoints += result.LeftPoints; rightPoints += result.RightPoints; battles++;
            }
            Check(Math.Abs(leftPoints - rightPoints) / (battles * 3) <= 1d / 9 + .0001, "uniform strategy does not overwhelmingly favor an archetype");
        }
        var offensive = profiles.Single(wand => wand.CombatArchetype == "offensive");
        var balanced = profiles.Single(wand => wand.CombatArchetype == "balanced");
        Check(Resolve(offensive, balanced, "strike", "strike", "x").LeftScore > Resolve(offensive, balanced, "strike", "strike", "x").RightScore,
            "offensive personality wins a direct clash");
        Check(Resolve(offensive, balanced, "trick", "trick", "x").LeftScore < Resolve(offensive, balanced, "trick", "trick", "x").RightScore,
            "offensive personality pays a real deception weakness");
        var longIdentity = offensive with { DisplayName = string.Concat(Enumerable.Repeat("🪄", 50)), WandName = string.Concat(Enumerable.Repeat("🌠", 50)),
            Spells = offensive.Spells!.Select(spell => spell with { Name = string.Concat(Enumerable.Repeat("🌠", 50)) }).ToArray() };
        var longRound = Resolve(longIdentity, longIdentity, "strike", "shield", "long");
        var document = new DuelDocument { Id = "id", DeviceId = "device", EveningId = "evening", Seed = "seed", Mode = "autoplay",
            Left = longIdentity, Right = longIdentity, Commitments = [], Rounds = [longRound, longRound, longRound],
            Result = DuelRules.Result([longRound, longRound, longRound], false), RulesVersion = DuelRules.CharacterVersion };
        foreach (var stage in new[] { "intro_dispatch", "round_dispatch", "final_dispatch" })
            Check(System.Text.Encoding.UTF8.GetByteCount(DuelPersonalizedNarration.PhysicalLine(document, stage)) <= 500,
                "v3 physical narration preserves existing bridge byte limits even with long Unicode names");
        var fakePower = DuelRules.Resolve("sim", 1, balanced, balanced, new("strike", "shield", 9999, -9999), "strike", "strike", DuelRules.CharacterVersion);
        Check(fakePower.LeftScore == fakePower.RightScore, "v3 ignores even corrupted legacy random jitter");
        Console.WriteLine($"Duel v3 balance: {comparisons} pair/action matrices, 16 minimax grids and 11,664 complete three-round battles passed.");
    }
    private static DuelRound Resolve(DuelIdentitySnapshot left, DuelIdentitySnapshot right, string a, string b, string seed, int round = 1) =>
        DuelRules.Resolve(seed, round, left, right, DuelRules.Commit(seed, round, DuelRules.CharacterVersion), a, b, DuelRules.CharacterVersion);
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Duel balance: " + message); }
}
