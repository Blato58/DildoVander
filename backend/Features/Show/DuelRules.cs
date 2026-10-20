using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Kufr.Backend.Features.Show;

internal sealed record DuelIdentitySnapshot(string IdentityId, string DisplayName, string WandId,
    int Strength, int Cunning, int Defense);
internal sealed record DuelRound(int Number, string LeftAction, string RightAction, int LeftScore,
    int RightScore, bool LeftAutomatic, bool RightAutomatic, string Quip, string Mishap);
internal sealed record DuelResult(double LeftPoints, double RightPoints, string Winner, string Ending, bool Exhibition);
internal sealed record DuelCommitment(string LeftAction, string RightAction, int LeftJitter, int RightJitter);

// No model output participates in scoring. The hash-counter stream and profile mapping
// are part of this version; changes require a new version and new fixtures.
internal static class DuelRules
{
    public const string Version = "duel-sha256-counter-v1";
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

    public static DuelCommitment Commit(string seed, int round) => new(
        Actions[Number(seed, $"{round}:left:action") % 3],
        Actions[Number(seed, $"{round}:right:action") % 3],
        (int)(Number(seed, $"{round}:left:jitter") % 3) - 1,
        (int)(Number(seed, $"{round}:right:jitter") % 3) - 1);

    public static DuelRound Resolve(string seed, int round, DuelIdentitySnapshot left, DuelIdentitySnapshot right,
        DuelCommitment commitment, string? leftChoice, string? rightChoice)
    {
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
            Quips[Number(seed, $"{round}:quip") % Quips.Length],
            Mishaps[Number(seed, $"{round}:mishap") % Mishaps.Length]);
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
    private static uint Number(string seed, string counter) => BinaryPrimitives.ReadUInt32LittleEndian(
        SHA256.HashData(Encoding.UTF8.GetBytes($"{Version}:{seed}:{counter}")));
}

internal sealed class DuelException(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
