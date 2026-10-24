using System.Collections.Concurrent;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Show;

namespace Kufr.Backend.Features.TestingSessions;

internal sealed class TestingDuelIdentities(EveningMemoryStore evenings) : IDuelIdentitySource
{
    internal Func<string, string, DuelIdentitySnapshot?>? Resolve { get; set; }
    public bool IsActiveEvening(string deviceId, string eveningId) => evenings.ReadActiveEveningId(deviceId) == eveningId;
    public bool IsOpenEvening(string deviceId, string eveningId) => IsActiveEvening(deviceId, eveningId)
        && evenings.ReadChronicle(deviceId, eveningId)?.Provisional == true;
    public DuelIdentitySnapshot? Read(string identityId, string eveningId, string deviceId) =>
        IsActiveEvening(deviceId, eveningId) ? Resolve?.Invoke(identityId, deviceId) : null;
}

// Only the physical show slot is virtual. Combat, grants, consent, deterministic
// resolution, persistence and optional provider commentary remain DuelStore's.
internal sealed class TestingDuelSlot : IDuelShowSlot
{
    private readonly ConcurrentDictionary<string, string> occupied = new(StringComparer.Ordinal);
    internal Action<string, string, int, string>? Observe { get; set; }
    public bool TryOccupy(string deviceId, string eveningId, string duelId) =>
        occupied.TryAdd(deviceId, duelId) || IsCurrent(deviceId, duelId);
    public bool IsCurrent(string deviceId, string duelId) => occupied.TryGetValue(deviceId, out var current) && current == duelId;
    public void PublishRound(string deviceId, string duelId, int round, string quip)
    {
        if (IsCurrent(deviceId, duelId)) Observe?.Invoke(deviceId, duelId, round, quip);
    }
    public void Release(string deviceId, string duelId)
    {
        occupied.TryRemove(new KeyValuePair<string, string>(deviceId, duelId));
    }
    public DuelNarrationBinding BindNarration(string deviceId) => new("web_only", "virtual_hardware_no_spoken_confirmation");
    public bool EnqueueNarration(string deviceId, string duelId, int round, string scene, string wandId, string text) => false;
    public string NarrationState(string deviceId, string duelId) => "unavailable";
}
