namespace Kufr.Backend.Features.Keepsakes;

internal sealed record KeepsakeIssueSnapshot(string OutcomeId, string DeviceId, int Slot, string CatalogId,
    string OfficialName, string CatalogFacts, string PhysicalEvidence, string CatalogVersion = "ollivander-private-party-v1",
    string? EveningId = null, string? SourceConversationId = null);
internal sealed record IssuedKeepsake(string Id);
internal sealed record KeepsakeSession(string Token, string CsrfToken);
internal sealed record KeepsakeLink(string Token, DateTimeOffset ExpiresAt, string KeepsakeId, string? EditionId = null);
internal sealed record KeepsakeBrief(string? DisplayName = null, string? PersonalMotif = null, bool PersonalizationConsent = false);
internal sealed record WandDesignSpec(string Shape, string Handle, string Ornament, string Material, string MagicalTheme);
internal sealed record KeepsakeStory(string Title, string Text, WandDesignSpec Design);
internal sealed record KeepsakeAsset(string Id, string Kind, string MediaType, string Sha256, long Length,
    string? EditionId, string? UploadId, DateTimeOffset CreatedAt);
internal sealed record KeepsakeEdition(string Id, string JobId, KeepsakeStory Story, string ArtworkAssetId,
    bool Synthetic, string Provider, string Model, string Prompt, DateTimeOffset CreatedAt,
    string PublicationState = "ready");
internal sealed record KeepsakePortrait(string Id, string JobId, string EditionId, string UploadId, string Variant,
    string AssetId, bool Synthetic, string Provider, string Model, string Prompt, DateTimeOffset CreatedAt);
internal sealed record KeepsakeUpload(string Id, string OriginalAssetId, string NormalizedAssetId,
    string SourceMediaType, int Width, int Height, DateTimeOffset CreatedAt);
internal sealed record KeepsakeJob(string Id, string Kind, string State, string? EditionId,
    string? UploadId, string? Variant, int Fence, DateTimeOffset CreatedAt, string? ErrorCode = null);
internal sealed record KeepsakeView(string Id, KeepsakeIssueSnapshot Snapshot, string RetentionMode,
    string State, IReadOnlyList<KeepsakeEdition> Editions, IReadOnlyList<KeepsakePortrait> Portraits,
    IReadOnlyList<KeepsakeUpload> Uploads, IReadOnlyList<KeepsakeJob> Jobs, IReadOnlyList<KeepsakeAsset> Assets,
    bool ReadOnly, string? SharedEditionId, int Revision = 0, bool PortraitConsent = false,
    int PortraitConsentVersion = 0, IReadOnlyList<WizardIdentityCard>? WizardIdentities = null,
    IReadOnlyList<ProphecyView>? Prophecies = null, KeepsakeReadiness? Readiness = null);
internal sealed record KeepsakeAssetContent(KeepsakeAsset Asset, byte[] Bytes);
internal sealed record KeepsakeDeletionReceipt(string Id, string Scope, string LocalState,
    string ProviderState, string BackupState, DateTimeOffset RequestedAt);
internal enum KeepsakeScope { Read, Write, Photo, Delete }
internal enum KeepsakeDeleteScope { All, PhotosAndPortraits }
internal sealed class KeepsakeException(string code, int status = 400) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

internal sealed record KeepsakeProviderRequest(string KeepsakeId, KeepsakeIssueSnapshot Snapshot,
    KeepsakeBrief Brief, string JobId, string? EditionId, string? UploadId, string? Variant,
    byte[]? NormalizedPhoto, KeepsakeStory? ExistingStory)
{
    public Func<string, string, string, bool>? RecordStage { get; init; }
}
internal sealed record KeepsakeProviderOutput(KeepsakeStory? Story, byte[] ImageBytes, string MediaType,
    string Provider, string Model, string Prompt, string ContentResponse, bool Synthetic, WizardIdentityContent? Identity = null);
internal interface IKeepsakeProvider
{
    Task<KeepsakeProviderOutput> GenerateAsync(KeepsakeProviderRequest request, CancellationToken cancellationToken);
}
