using System.Text;
using System.Text.RegularExpressions;

namespace Kufr.Backend.Features.Show;

internal sealed partial class ShowCoordinator
{
    // Catalog fiction is offered only after the existing selection service has
    // chosen a slot. Observed history needs transcript phrase plus player drain;
    // scheduled history alone never claims the model spoke an offered line.
    internal ShowLine[] OfferWandRepertoire(string deviceId, string generation, int slot, long decisionRevision)
    {
        lock (gate)
        {
            var runtime = Get(deviceId);
            if (runtime.State.SessionGeneration != generation || slot is < 1 or > 6) { return []; }
            var profile = ShowRepertoire.Wands.Single(wand => wand.SlotId == slot);
            var kind = runtime.State.SceneId switch
            {
                "wand-rebellion" => "fictional-refusal",
                "shelf-dispute" => "rivalry",
                "professional-funeral" => "overlooked",
                _ => "acceptance"
            };
            ShowLine Pick(string category)
            {
                var candidates = profile.Lines.Where(line => line.Kind == category).ToArray();
                var key = profile.CatalogWandId + "/" + category;
                var count = runtime.WandCounters.GetValueOrDefault(key);
                runtime.WandCounters[key] = (count + 1) % 64;
                return candidates[Math.Abs(count % candidates.Length)];
            }
            var lines = new[] { Pick("introduction"), Pick(kind), Pick("closing") };
            runtime.PendingWandLines = lines.Select(line => line.Id).ToArray();
            runtime.PendingWandSlot = slot;
            runtime.PendingWandRevision = decisionRevision;
            foreach (var line in lines)
            {
                runtime.ScheduledWandLines.Enqueue(line.Id);
                while (runtime.ScheduledWandLines.Count > 24) { runtime.ScheduledWandLines.Dequeue(); }
            }
            Update(runtime, runtime.State);
            return lines;
        }
    }

    internal void CancelWandRepertoire(string deviceId, string generation)
    {
        lock (gate)
        {
            var runtime = Get(deviceId);
            if (runtime.State.SessionGeneration != generation) { return; }
            runtime.PendingWandLines = [];
            runtime.PendingWandSlot = null;
        }
    }

    internal void RecordWandSpeechProxy(string deviceId, string generation, int slot, long decisionRevision,
        string observedAssistantText)
    {
        lock (gate)
        {
            var runtime = Get(deviceId);
            if (runtime.State.SessionGeneration != generation || runtime.PendingWandSlot != slot
                || runtime.PendingWandRevision != decisionRevision) { return; }
            var actual = " " + Normalize(observedAssistantText) + " ";
            var profile = ShowRepertoire.Wands.Single(wand => wand.SlotId == slot);
            foreach (var id in runtime.PendingWandLines)
            {
                var line = profile.Lines.Single(line => line.Id == id);
                if (!actual.Contains(" " + Normalize(line.Text) + " ", StringComparison.Ordinal)) { continue; }
                runtime.WandLines.Enqueue(id);
                while (runtime.WandLines.Count > 24) { runtime.WandLines.Dequeue(); }
            }
            runtime.PendingWandLines = [];
            runtime.PendingWandSlot = null;
            Update(runtime, runtime.State);
        }
    }

    private static string Normalize(string text) => Regex.Replace(text.Normalize(NormalizationForm.FormC)
        .ToLowerInvariant(), @"[^\p{L}\p{Nd}]+", " ", RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100)).Trim();
}
