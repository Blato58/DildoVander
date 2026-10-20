namespace Kufr.Backend.Features.Character;

internal enum CharacterTone { Reserved, Dry, Adult, Chaos }
internal enum FinaleProfile { Named, Limited, Blocked }
internal enum CharacterTempo { Standard, Express }
internal enum CharacterEngagement { Conversational, LowPressure }
internal enum CharacterAudio { Normal, Degraded, Unavailable }
internal enum CharacterPhase
{
    Idle, Opening, Listening, Responding, Deciding, Ready, Presenting, Repair, Closing, Closed, Stopped
}
internal enum CharacterFinaleKind { Callback, Contrast, Ceremony }
internal enum CharacterAnchorKind { GuestStatement, Preference, Fiction, VisualObservation }
internal enum CharacterSignalKind
{
    RequestExpress, ToneDown, ToneUp, NoSwearing, WithdrawRoast,
    RevokeTopic, RevokeImage, RequestEnd, CorrectEvidence, AttributeToOther, MarkFiction, RequestStopSpeech
}
internal enum SceneIntent { Wake, Consider, ChoiceAccent, Farewell }
internal enum CharacterSpeechIntent { None, Question, Reply, Repair, Finale, Closing }

internal sealed record CharacterPolicy(
    bool Enabled = false,
    CharacterTone HostTone = CharacterTone.Adult,
    FinaleProfile FinaleProfile = FinaleProfile.Blocked,
    string Version = "dv01-director-v1");

// This is transient provenance, not a personal profile or a claim of semantic accuracy.
internal sealed record CharacterAnchor(
    string Id,
    CharacterAnchorKind Kind,
    string Text,
    string SourceId,
    long SourceRevision,
    long EvidenceRevision,
    long? PermissionRevision = null);

internal sealed record CharacterSignal(
    string SessionGeneration,
    string SourceId,
    long SourceRevision,
    CharacterSignalKind Kind,
    int QuoteStart,
    int QuoteLength,
    string? TargetAnchorId = null);

internal readonly record struct CharacterInputChange(
    bool CancelResponse = false,
    bool EndSession = false,
    bool EvidenceChanged = false,
    bool ImageRevoked = false);

internal sealed record CharacterDirective(
    string PromptText,
    CharacterPhase Phase,
    CharacterTone Tone,
    CharacterTempo Tempo,
    CharacterEngagement Engagement,
    int QuestionBudget,
    int WordBudget,
    CharacterFinaleKind FinaleKind,
    string StyleId,
    CharacterAnchor? Anchor,
    SceneIntent? SceneIntent = null,
    CharacterSpeechIntent SpeechIntent = CharacterSpeechIntent.None,
    long DirectorRevision = 0,
    long SourceRevision = 0);
