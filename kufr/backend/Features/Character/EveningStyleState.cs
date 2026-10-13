namespace Kufr.Backend.Features.Character;

// Updated by the session owner only after playback starts (or its documented proxy).
// No utterances, guest/session identifiers, source references or personal motifs are accepted.
internal sealed class EveningStyleState
{
    private readonly Queue<string> openings = new();
    private readonly Queue<string> mechanisms = new();
    private readonly byte[] loreCounts = new byte[6];

    internal static readonly IReadOnlyList<string> OpeningIds =
        Array.AsReadOnly(new[] { "understatement", "craft_classification", "bureaucracy" });
    internal static readonly IReadOnlyList<string> MechanismIds =
        Array.AsReadOnly(new[] { "understatement", "craft_classification", "bureaucracy", "status_flip", "budget_absurdity", "self_mockery" });

    internal IReadOnlyList<string> Snapshot() => openings.Concat(mechanisms)
        .Concat(Enumerable.Range(1, 6).SelectMany(slot => Enumerable.Repeat($"lore-{slot}", loreCounts[slot - 1])))
        .ToArray();

    internal bool Record(string styleId, bool opening = false)
    {
        if (opening && OpeningIds.Contains(styleId, StringComparer.Ordinal))
        {
            Enqueue(openings, styleId, 6);
            return true;
        }
        if (MechanismIds.Contains(styleId, StringComparer.Ordinal))
        {
            Enqueue(mechanisms, styleId, 12);
            return true;
        }
        for (var slot = 1; slot <= 6; slot++)
        {
            if (styleId == $"lore-{slot}")
            {
                loreCounts[slot - 1] = (byte)Math.Min(3, loreCounts[slot - 1] + 1);
                return true;
            }
        }
        return false;
    }

    internal void Reset()
    {
        openings.Clear();
        mechanisms.Clear();
        Array.Clear(loreCounts);
    }

    internal static bool IsAllowed(string styleId) => OpeningIds.Contains(styleId, StringComparer.Ordinal)
        || MechanismIds.Contains(styleId, StringComparer.Ordinal)
        || Enumerable.Range(1, 6).Any(slot => styleId == $"lore-{slot}");

    private static void Enqueue(Queue<string> queue, string value, int limit)
    {
        queue.Enqueue(value);
        while (queue.Count > limit)
        {
            queue.Dequeue();
        }
    }
}
