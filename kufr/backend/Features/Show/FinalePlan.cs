using System.Collections.Immutable;

namespace Kufr.Backend.Features.Show;

// Technical references only: never copy an anchor's personal text into the plan.
internal sealed record FinaleAnchorReference(string AnchorId, string SourceId, long SourceRevision,
    long EvidenceRevision, long? PermissionRevision = null);

internal sealed record FinaleSceneSnapshot(string SceneId, int Version, string Variant,
    ImmutableArray<string> Phases, ImmutableArray<string> RequiredCapabilities, int Priority,
    int MaximumDurationMs, ImmutableArray<string> AllowedFinaleProfiles, ImmutableArray<string> Preconditions,
    string Cancellation, string Fallback, ImmutableArray<ShowCue> Cues)
{
    internal static FinaleSceneSnapshot Capture(ShowScene scene) => new(scene.SceneId, scene.Version, scene.Variant,
        scene.Phases.ToImmutableArray(), scene.RequiredCapabilities.ToImmutableArray(), scene.Priority,
        scene.MaximumDurationMs, scene.AllowedFinaleProfiles.ToImmutableArray(), scene.Preconditions.ToImmutableArray(),
        scene.Cancellation, scene.Fallback, scene.Cues.ToImmutableArray());
}

internal sealed record FinalePlan(int SchemaVersion, string FinaleId, string DeviceId, string? EveningId,
    string ProposalId, string OperationId, string SessionId, string? SessionGeneration,
    int SlotId, string CatalogWandId, string CatalogVersion, int DeviceRevision,
    Guid DeviceBootId, int DeviceGeneration, Guid ClientTurnId, long SelectionRevision,
    string SceneVersion, string PersonaVersion, string Profile, string Archetype,
    ImmutableArray<FinaleAnchorReference> ApprovedAnchorReferences, DateTimeOffset Deadline,
    string Fallback, FinaleSceneSnapshot CuePlan, string State = "proposal-committed", bool Locked = false,
    string? ObservedOutcomeId = null, string? PresentationEvidence = null);

internal sealed record FinaleBindingResult(string Status, string Code, FinalePlan? Plan);
