using Kufr.Backend.Features.GptLive;

namespace Kufr.Backend.Features.Conversations;

internal sealed partial class ConversationStore
{
    internal bool AppendRealtimeTranscript(RealtimeCall call, string eventId, string visitor, string reply)
    {
        lock (gate)
        {
            if (!IsRealtimeTurnCurrent(call) || string.IsNullOrWhiteSpace(eventId))
            {
                return false;
            }
            if (call.TranscriptEvents.Contains(eventId))
            {
                return true;
            }

            // Archive each finalized event before terminal selection. An
            // interrupted interaction must retain what was actually said.
            if (call.EveningId is not null)
            {
                var chunks = Math.Max((visitor.Length + 999) / 1000, (reply.Length + 499) / 500);
                for (var index = 0; index < chunks; index++)
                {
                    if (!eveningMemory!.Append(call.Correlation.DeviceId, call.EveningId,
                        call.OperationId + ":event:" + eventId + ":" + index,
                        call.Correlation.ConversationId, Slice(visitor, index * 1000, 1000), Slice(reply, index * 500, 500),
                        expectedDeletionEpoch: call.MemoryDeletionEpoch))
                    {
                        return false;
                    }
                }
            }
            if (!IsRealtimeTurnCurrent(call))
            {
                return false;
            }
            conversations[call.Correlation.ConversationId].History.Add(new(visitor, reply));
            call.TranscriptEvents.Add(eventId);
            return true;
        }
    }

    internal bool AppendRealtimeTranscript(RealtimeCall call, LiveTranscriptSegment segment)
    {
        lock (gate)
        {
            if (!IsRealtimeTurnCurrent(call))
            {
                return false;
            }
            if (LiveMemoryAccess(call) is { } access)
            {
                eveningMemory!.AppendTimedTranscripts(access, call.SessionGeneration, [segment], call.MemoryDeletionEpoch);
            }
            return AppendRealtimeTranscript(call, segment.EventId ?? segment.Reference,
                segment.Speaker is "visitor" or "user" ? segment.Text : "",
                segment.Speaker == "assistant" ? segment.Text : "");
        }
    }

    private static string Slice(string text, int start, int count) =>
        start >= text.Length ? "" : text.Substring(start, Math.Min(count, text.Length - start));
}
