namespace Kufr.Backend.Features.GptLive;

// The provider has no completed-input-turn event. Observe PCM separately from
// transcript delivery so a network gap cannot trigger a backend interruption.
internal sealed class LiveInputActivity(TimeProvider clock, int threshold)
{
    private long lastSpeech = -1;

    internal void Observe(byte[] pcm)
    {
        long energy = 0;
        var samples = pcm.Length / 2;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (short)(pcm[i] | pcm[i + 1] << 8);
            energy += (long)sample * sample;
        }
        if (samples > 0 && energy >= (long)threshold * threshold * samples)
            Interlocked.Exchange(ref lastSpeech, clock.GetTimestamp());
    }

    internal bool QuietFor(int milliseconds)
    {
        var last = Interlocked.Read(ref lastSpeech);
        return last < 0 || clock.GetElapsedTime(last) >= TimeSpan.FromMilliseconds(milliseconds);
    }
}
