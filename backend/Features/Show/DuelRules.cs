using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Kufr.Backend.Features.Wands;

namespace Kufr.Backend.Features.Show;

internal sealed record DuelIdentitySnapshot(string IdentityId, string DisplayName, string WandId,
    int Strength, int Cunning, int Defense, string? WandName = null, WandSpell[]? Spells = null,
    int? PhysicalSlot = null);
internal sealed record DuelRound(int Number, string LeftAction, string RightAction, int LeftScore,
    int RightScore, bool LeftAutomatic, bool RightAutomatic, string Quip, string Mishap);
internal sealed record DuelResult(double LeftPoints, double RightPoints, string Winner, string Ending, bool Exhibition);
internal sealed record DuelCommitment(string LeftAction, string RightAction, int LeftJitter, int RightJitter);

// No model output participates in scoring. The hash-counter stream and profile mapping
// are part of this version; changes require a new version and new fixtures.
internal static class DuelRules
{
    public const string Version = "duel-sha256-counter-v1";
    public const string PersonalizedVersion = "duel-personalized-v2";
    public static bool Supports(string version) => version is Version or PersonalizedVersion;
    public static readonly string[] Actions = ["strike", "trick", "shield"];
    private static readonly (int Strength, int Cunning, int Defense)[] Profiles =
        [(5, 3, 1), (5, 1, 3), (3, 5, 1), (1, 5, 3), (3, 1, 5), (1, 3, 5)];
    private static readonly string[] Mishaps =
    [
        "Rozhodčímu se rozmočil formulář. Body zůstávají čitelné.",
        "Neviditelný drak si odkašlal do mikrofonu.",
        "Kouzlo omylem orazítkovalo vlastní žádost.",
        "Ministerská sova přinesla účtenku za napětí.",
        "Štít na chvíli připomínal pokličku od guláše.",
        "Komise ztratila důstojnost, výsledek nikoli."
    ];
    private static readonly string[] Quips =
    [
        "Ministerstvo uznává zásah. Rozhodčí ho neviděl, ale zvuk byl drahej.",
        "Tohle byl učebnicový tah. Učebnici už raději stáhli.",
        "Magická technika přesná, papírování katastrofální.",
        "Publikum zadrželo dech. Hygiena zadržela připomínky.",
        "Takový obrat nezažila ani otočná židle děkana.",
        "Kouzlo prošlo kontrolou. Kontrola potřebuje dovolenou."
    ];

    internal static IReadOnlyList<string> ApprovedQuips => Quips;
    internal static IReadOnlyList<string> ApprovedMishaps => Mishaps;

    public static DuelIdentitySnapshot Snapshot(string id, string alias, string wandId)
    {
        var profile = Profiles[Number(wandId, "profile") % Profiles.Length];
        return new(id, alias, wandId, profile.Strength, profile.Cunning, profile.Defense);
    }

    // All personalized wands have the same server-owned power budget. Fictional
    // material, affinity, weakness and prose cannot increase numerical power.
    public static DuelIdentitySnapshot PersonalizedSnapshot(string id, string alias, string wandId,
        string name, int physicalSlot, WandSpell[] spells)
    {
        if (physicalSlot is < 1 or > 6 || string.IsNullOrWhiteSpace(wandId)
            || string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl)
            || spells is not { Length: 3 } || spells.Any(spell => spell is null)
            || !spells.Select(spell => spell.Action).Order().SequenceEqual(Actions.Order())
            || spells.Any(spell => string.IsNullOrWhiteSpace(spell.Name) || spell.Name.Length > 100
                || spell.Name.Any(char.IsControl) || string.IsNullOrWhiteSpace(spell.Description)
                || spell.Description.Length > 320 || spell.Description.Any(char.IsControl))
            || spells.Select(spell => spell.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
        { throw new DuelException(409, "wand_assignment_invalid"); }
        return new(id, alias, wandId, 3, 3, 3, name, spells.ToArray(), physicalSlot);
    }

    public static DuelCommitment Commit(string seed, int round, string rulesVersion = Version)
    {
        if (!Supports(rulesVersion) || round is < 1 or > 3) { throw new DuelException(409, "rules_version_unavailable"); }
        // V2 shares jitter between sides: identical actions always tie. The
        // original independent V1 jitter and hash prefix remain unchanged.
        var leftJitter = (int)(Number(seed, $"{round}:left:jitter", rulesVersion) % 3) - 1;
        return new(Actions[Number(seed, $"{round}:left:action", rulesVersion) % 3],
            Actions[Number(seed, $"{round}:right:action", rulesVersion) % 3], leftJitter,
            rulesVersion == PersonalizedVersion ? leftJitter : (int)(Number(seed, $"{round}:right:jitter", rulesVersion) % 3) - 1);
    }

    public static DuelRound Resolve(string seed, int round, DuelIdentitySnapshot left, DuelIdentitySnapshot right,
        DuelCommitment commitment, string? leftChoice, string? rightChoice, string rulesVersion = Version)
    {
        if (!Supports(rulesVersion)) { throw new DuelException(409, "rules_version_unavailable"); }
        var leftAction = leftChoice ?? commitment.LeftAction;
        var rightAction = rightChoice ?? commitment.RightAction;
        if (!Actions.Contains(leftAction) || !Actions.Contains(rightAction))
        {
            throw new DuelException(400, "invalid_action");
        }
        var advantage = Advantage(leftAction, rightAction);
        return new(round, leftAction, rightAction,
            Stat(left, leftAction) + advantage + commitment.LeftJitter,
            Stat(right, rightAction) - advantage + commitment.RightJitter,
            leftChoice is null, rightChoice is null,
            Quips[Number(seed, $"{round}:quip", rulesVersion) % Quips.Length],
            Mishaps[Number(seed, $"{round}:mishap", rulesVersion) % Mishaps.Length]);
    }

    public static DuelResult Result(IReadOnlyList<DuelRound> rounds, bool exhibition)
    {
        if (rounds.Count != 3) { throw new InvalidOperationException("A duel has exactly three rounds."); }
        var left = rounds.Sum(round => round.LeftScore > round.RightScore ? 1 : round.LeftScore == round.RightScore ? .5 : 0);
        var right = 3 - left;
        var winner = left > right ? "left" : right > left ? "right" : "draw";
        var ending = winner switch
        {
            "left" => "Levá strana vítězí. Pravá získává čestné právo podat nesmyslnou stížnost.",
            "right" => "Pravá strana vítězí. Levá dnes statečně otestovala hranice formuláře.",
            _ => "Remíza. Ministerstvo odmítá zaplatit druhou korunu a vyhlašuje společnou slávu."
        };
        return new(left, right, winner, ending, exhibition);
    }

    private static int Stat(DuelIdentitySnapshot identity, string action) => action switch
    {
        "strike" => identity.Strength, "trick" => identity.Cunning, "shield" => identity.Defense,
        _ => throw new DuelException(400, "invalid_action")
    };
    private static int Advantage(string left, string right) => left == right ? 0
        : (left, right) is ("strike", "trick") or ("trick", "shield") or ("shield", "strike") ? 2 : -2;
    private static uint Number(string seed, string counter, string version = Version) => BinaryPrimitives.ReadUInt32LittleEndian(
        SHA256.HashData(Encoding.UTF8.GetBytes($"{version}:{seed}:{counter}")));
}

internal sealed class DuelException(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
