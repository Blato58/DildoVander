using Kufr.Backend.Features.Conversations;

namespace Kufr.Backend.Features.Realtime;

internal static class VisualResponseWatcher
{
    internal static async Task RunAsync(ConversationStore conversations, RealtimeLiveSession session,
        Func<object, CancellationToken, Task> write, CancellationToken token,
        Func<int, CancellationToken, Task<long>>? cancel = null)
    {
        long observed = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var current = conversations.ReadVisualResponseRevision(session.DeviceId, session.ConversationId);
            if (current.Generation < 0) { return; }
            if (current.Generation != observed)
            {
                var generation = cancel is not null ? await cancel(current.VisualRevision, token) : session.CancelOutput();
                if (cancel is null)
                { await write(new { type = "audio_cancel", responseGeneration = generation, visualRevision = current.VisualRevision }, token); }
                session.ApplyVisualBarrier(current.Generation);
                if (session.ListeningInput is { } input)
                {
                    await write(new { type = "listening", inputId = input, responseGeneration = generation }, token);
                }
                observed = current.Generation;
            }
            await current.Changed.WaitAsync(token);
        }
    }
}
