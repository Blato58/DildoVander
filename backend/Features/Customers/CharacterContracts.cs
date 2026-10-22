namespace Kufr.Backend.Features.Customers;

internal sealed record CharacterUserInput(string Name, string World, string? Version = null,
    string? Description = null, string? Language = null);
internal sealed record CharacterSource(string Title, string Url);
internal sealed record CharacterFact(string Text, string[] SourceUrls);
internal sealed record CharacterEnrichment(string RecognizedName, string World, string Summary,
    CharacterFact[] Facts, string[] ConversationIdeas, string[] Ambiguities, bool NeedsClarification,
    CharacterSource[] Sources, string Model, string PromptVersion, long ProcessedAt, long InputRevision,
    string EvidenceKind, int InputTokens = 0, int OutputTokens = 0);
internal sealed record CharacterProfile(string ProfileId, long Revision, CharacterUserInput UserInput,
    string Status, CharacterEnrichment? Enrichment, string? ErrorCode, long UpdatedAt);
internal sealed record CharacterWriteRequest(long ExpectedRevision, CharacterUserInput UserInput,
    string? ExpectedProfileId = null, bool NewCharacter = false, bool OpenAiConsent = false);
internal sealed record CharacterCorrectionRequest(long ExpectedRevision, string ExpectedProfileId, bool OpenAiConsent = false);
internal sealed record CharacterConversationSnapshot(string ProfileId, long Revision, CharacterUserInput UserInput,
    CharacterEnrichment? Enrichment, long CapturedAt);
internal sealed record CharacterJob(string Id, string AccountId, string ProfileId, long Revision,
    CharacterUserInput UserInput, string? RejectedInterpretation, string LeaseToken, int Attempt, bool OpenAiConsent = false);
internal sealed record CharacterJobSummary(string ProfileId, long Revision, string Status, int Attempt, string? ErrorCode, long UpdatedAt);
internal sealed class CharacterException(string code, int status = 400) : Exception(code)
{
    internal string Code { get; } = code;
    internal int Status { get; } = status;
}

internal interface ICharacterEnrichmentProvider
{
    Task<CharacterEnrichment> EnrichAsync(CharacterJob job, CancellationToken cancellationToken);
}
