using Kufr.Backend.Features.Realtime;

namespace Kufr.Backend.Features.GptLive;

internal sealed record LiveTranscript(string Visitor, string Reply);
internal sealed record LiveOutcome(RealtimeReply Reply, int Questions, IReadOnlyList<LiveTranscript> Transcript)
{
    internal bool Autonomous { get; init; }
    internal bool PlaybackVerified { get; init; }
    internal long DecisionRevision { get; init; }
    internal WandDecisionResult? Decision { get; init; }
    internal IReadOnlyList<LiveTranscriptSegment> Segments { get; init; } = [];
}

