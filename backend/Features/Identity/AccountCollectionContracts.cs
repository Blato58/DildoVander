using Kufr.Backend.Features.Keepsakes;

namespace Kufr.Backend.Features.Identity;

internal sealed record AccountCollectionPage(int SchemaVersion, IReadOnlyList<AccountCollectionItem> Items,
    string? NextCursor);

internal sealed record AccountCollectionItem(string Locator, long LinkedAt, string? OfficialName,
    string? StoryTitle, bool HasPortrait, string AccessKind, bool CanManage, bool CanReadPrivate,
    bool CanUnlink, KeepsakeReadiness Readiness, string? CharacterName, long? EveningAt);

internal sealed record AccountCollectionCursor(int SchemaVersion, string AccountId, int Limit,
    string? Query, string Access, long OwnedWatermark, long LinkedWatermark,
    long BeforeLinkedAt, string BeforeLocator);

internal sealed record AccountCollectionCandidate(string Locator, long LinkedAt, bool OriginalOwner,
    int Epoch, string? CustomerSessionId, long SourceRevision);
