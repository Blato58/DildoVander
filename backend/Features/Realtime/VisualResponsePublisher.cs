using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Diagnostics;

namespace Kufr.Backend.Features.Realtime;

internal static class VisualResponsePublisher
{
    // Capture source generations before waiting for the serialized writer. A
    // DELETE while queued must prevent domain commit as well as publication.
    internal static async Task PublishAsync(ConversationStore conversations, RealtimeLiveSession session,
        SemaphoreSlim writes, long visualGeneration, long outputGeneration,
        Func<object> commit, Func<object, CancellationToken, Task> beginWrite, CancellationToken token)
    {
        await writes.WaitAsync(token);
        try
        {
            await session.EnsureHardwareAuthorityAsync(token);
            await conversations.PublishVisualResponse(session.DeviceId, session.ConversationId, visualGeneration, () =>
            {
                if (session.OutputGeneration != outputGeneration) { throw new VisualResponseRevokedException(); }
                return beginWrite(commit(), token);
            });
        }
        finally { writes.Release(); }
    }
}
