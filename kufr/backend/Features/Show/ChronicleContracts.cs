namespace Kufr.Backend.Features.Show;

// Only server-authored facts enter this ledger. It deliberately has no transcript,
// prophecy, photograph, alias or free-form public-story field.
internal enum ShowMemoryEventKind
{
    VisitStarted, VisitCompleted, PresentationConfirmed, VisitInterrupted,
    IdentityCompleted, DuelCompleted, RunningJokeUsed, EveningClosed
}

internal sealed record ShowMemoryEvent(
    string EventId, ShowMemoryEventKind Kind, string DeviceId, string EveningId,
    string SourceId, long SourceRevision, string Provenance,
    string? ConversationId = null, int? Slot = null, string? Evidence = null,
    string? JokeId = null);

internal sealed record ChronicleWand(int Slot, string CatalogId, string OfficialName, int ConfirmedPresentations);
internal sealed record EveningLoreItem(string Id, string Text, int DocumentedUses, int MaximumUses, int CooldownVisits);
internal sealed record EveningLore(int SchemaVersion, string EveningId, IReadOnlyList<EveningLoreItem> Motifs);
internal sealed record ChronicleSnapshot(
    int SchemaVersion, string EveningId, int Revision, bool Provisional,
    DateTimeOffset CreatedAt, int CompletedVisits, int ConfirmedPresentations,
    int CompletedDuels, int InterruptedVisits, int UnresolvedVisits,
    IReadOnlyList<ChronicleWand> Wands, IReadOnlyList<int> MostPopularSlots,
    string Narrative, string Retention = "manual_only");

internal sealed record ChronicleAdministration(ChronicleSnapshot Snapshot, int ArchivedEditions,
    string ProviderCleanup = "not_requested", string BackupCleanup = "not_confirmed");
