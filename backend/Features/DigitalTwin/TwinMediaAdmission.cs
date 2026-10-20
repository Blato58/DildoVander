using System.Collections.Concurrent;

namespace Kufr.Backend.Features.DigitalTwin;

internal readonly record struct TwinAdmissionIdentity(string OwnerId, Guid BootId, Guid OperationId);

// Memory belongs to one known simulated instance. Never evict an unexpired
// cancellation: saturation closes fresh admission rather than reviving a UUID.
internal sealed class TwinAdmissionCancellations
{
    private const int Capacity = 64;
    private readonly Lock gate = new();
    private sealed class Deadline(Func<DateTimeOffset> current)
    {
        internal DateTimeOffset Minimum;
        internal DateTimeOffset Expires
        {
            get { var value = current(); return value > Minimum ? value : Minimum; }
        }
    }
    private readonly Dictionary<TwinAdmissionIdentity, Deadline> entries = new();
    private readonly Dictionary<string, Deadline> overflowOwners = new(StringComparer.Ordinal);
    private DateTimeOffset overflowUntil;

    internal bool Record(TwinAdmissionIdentity identity, DateTimeOffset expires, DateTimeOffset now)
        => Record(identity, () => expires, now);
    internal bool Record(TwinAdmissionIdentity identity, Func<DateTimeOffset> expiry, DateTimeOffset now)
    {
        lock (gate)
        {
            // Read the authenticated deadline under the same lock as renewal,
            // so a cancellation captured before login cannot expire early.
            var expires = expiry();
            Prune(now);
            if (expires <= now) { return false; }
            if (entries.ContainsKey(identity)) { return true; }
            if (entries.Count >= Capacity)
            {
                // An active matching actor still cancels. This bounded barrier
                // remembers the unavailable slot until its owner expires.
                if (overflowOwners.TryGetValue(identity.OwnerId, out var previous))
                { if (expires > previous.Minimum) { previous.Minimum = expires; } }
                else if (overflowOwners.Count < Capacity) { overflowOwners.Add(identity.OwnerId, new Deadline(expiry)); }
                else { overflowUntil = DateTimeOffset.MaxValue; } // Fail closed until the service restarts.
                return false;
            }
            entries.Add(identity, new Deadline(expiry));
            return true;
        }
    }
    internal string? Error(TwinAdmissionIdentity identity, DateTimeOffset now, bool fresh)
    {
        lock (gate)
        {
            Prune(now);
            if (entries.ContainsKey(identity)) { return "media_cancelled"; }
            return fresh && (entries.Count >= Capacity || overflowOwners.Count > 0 || overflowUntil > now) ? "media_cancellation_capacity" : null;
        }
    }
    internal void RenewOwner(string ownerId, DateTimeOffset expires, DateTimeOffset now)
    {
        lock (gate)
        {
            foreach (var identity in entries.Keys.Where(identity => identity.OwnerId == ownerId).ToArray())
            { if (entries[identity].Minimum < expires) { entries[identity].Minimum = expires; } }
            if (overflowOwners.TryGetValue(ownerId, out var overflow) && overflow.Minimum < expires)
            { overflow.Minimum = expires; }
        }
    }
    private void Prune(DateTimeOffset now)
    {
        // UUIDs do not establish boot ordering. Retain entries until owner
        // expiry so an in-flight old-boot request cannot erase newer memory.
        foreach (var expired in entries.Where(entry => entry.Value.Expires <= now).Select(entry => entry.Key).ToArray())
        { entries.Remove(expired); }
        foreach (var expired in overflowOwners.Where(entry => entry.Value.Expires <= now).Select(entry => entry.Key).ToArray())
        { overflowOwners.Remove(expired); }
    }
}

internal sealed partial class TwinMediaSessions
{
    private readonly ConcurrentDictionary<string, TwinAdmissionCancellations> cancellations = new(StringComparer.Ordinal);
    private static bool CanonicalAdmissionUuid(string? value, out Guid parsed) => Guid.TryParseExact(value, "D", out parsed)
        && parsed != Guid.Empty && value == parsed.ToString("D");
    private string? AdmissionScope(string id, TwinSession owner, string bootId)
    {
        if (owner.Principal.Role != "control" || owner.Principal.Mode != "simulation"
            || owner.Principal.InstanceId != id || !hosts.IsSimulation(id) || owner.Expires <= clock.GetUtcNow())
        { return "media_owner_required"; }
        return snapshots.Read(id)?.Snapshot.Source.BootId == bootId ? null : "media_boot_mismatch";
    }
    private string? AdmissionError(string id, TwinSession owner, string bootId, Guid boot, Guid operation, bool fresh)
    {
        var scope = AdmissionScope(id, owner, bootId);
        if (scope is not null) { return scope; }
        return cancellations.TryGetValue(id, out var memory)
            ? memory.Error(new(owner.Id, boot, operation), clock.GetUtcNow(), fresh) : null;
    }
    private async Task<string?> CancelAdmissionAsync(string id, TwinSession owner, TwinMediaCancel cancel)
    {
        if (!CanonicalAdmissionUuid(cancel.OperationId, out var operation) || !CanonicalAdmissionUuid(cancel.ExpectedBootId, out var boot))
        { return "media_admission_mismatch"; }
        var scope = AdmissionScope(id, owner, cancel.ExpectedBootId!);
        if (scope is not null) { return scope; }
        var memory = cancellations.GetOrAdd(id, _ => new TwinAdmissionCancellations());
        // Record before inspecting the actor. The memory lock is released
        // before actor.Gate; cancellation never takes the settings/manager lock.
        var recorded = memory.Record(new(owner.Id, boot, operation), () => owner.Expires, clock.GetUtcNow());
        if (actors.TryGetValue(id, out var actor) && actor.OwnerId == owner.Id
            && actor.OperationId == cancel.OperationId && actor.BootId == cancel.ExpectedBootId)
        {
            await CancelActorAsync(actor);
            return null;
        }
        return recorded ? null : "media_cancellation_capacity";
    }
}
