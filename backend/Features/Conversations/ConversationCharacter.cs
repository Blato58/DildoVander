using Kufr.Backend.Features.Realtime;
using Kufr.Backend.Features.Character;

namespace Kufr.Backend.Features.Conversations;

internal sealed partial class ConversationStore
{
    // At most 32 event/device scopes with bounded anonymous style IDs. This is
    // process-local show variety, never guest memory or a dispatch journal.
    private readonly Dictionary<string, EveningStyleState> characterStyles = new(StringComparer.Ordinal);
    internal IReadOnlyList<string> CharacterStyles(RealtimeCall call)
    {
        lock (gate)
        {
            var key = call.EveningId ?? call.Correlation.DeviceId;
            return characterStyles.TryGetValue(key, out var history) ? history.Snapshot() : [];
        }
    }
    internal void RecordCharacterStyle(RealtimeCall call, string style, bool opening = false)
    {
        lock (gate)
        {
            if (!IsRealtimeTurnCurrent(call) || !EveningStyleState.IsAllowed(style)) { return; }
            var key = call.EveningId ?? call.Correlation.DeviceId;
            if (!characterStyles.TryGetValue(key, out var history))
            {
                if (characterStyles.Count >= 32) { characterStyles.Remove(characterStyles.Keys.First()); }
                characterStyles[key] = history = new();
            }
            history.Record(style, opening);
        }
    }
}
