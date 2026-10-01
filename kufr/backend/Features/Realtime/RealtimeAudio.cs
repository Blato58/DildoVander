using System.Buffers.Binary;
using Kufr.Backend.Features.Conversations;

namespace Kufr.Backend.Features.Realtime;

internal static class RealtimeAudio
{
    internal static byte[] FromDeviceWav(byte[] wav)
    {
        if (TranscriptionUploadReader.ValidateWav(wav) != 200)
        {
            throw new InvalidDataException("Invalid input WAV.");
        }
        var offset = 12;
        while (!wav.AsSpan(offset, 4).SequenceEqual("data"u8))
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(offset + 4, 4));
            offset += 8 + length + length % 2;
        }
        var byteCount = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(offset + 4, 4));
        var pcm = wav.AsSpan(offset + 8, byteCount);
        var sampleCount = pcm.Length / 2;
        var output = new byte[(sampleCount * 3 / 4) * 2];
        if (output.Length < 4800)
        {
            throw new InvalidDataException("Realtime input needs at least 100 ms of audio.");
        }
        // Downsample 32 -> 24 kHz with an anti-alias windowed sinc. Never relabel
        // 32 kHz bytes as 24 kHz: that changes pitch and speech duration.
        const int radius = 16;
        const double cutoff = 0.75;
        var filters = new double[3][];
        for (var phase = 0; phase < filters.Length; phase++)
        {
            var coefficients = new double[radius * 2];
            var sum = 0.0;
            for (var tap = 0; tap < coefficients.Length; tap++)
            {
                var distance = tap - radius + 1 - phase / 3.0;
                var sinc = Math.Abs(distance) < 1e-12
                    ? cutoff
                    : Math.Sin(Math.PI * distance * cutoff) / (Math.PI * distance);
                var window = 0.42 + 0.5 * Math.Cos(Math.PI * distance / radius)
                    + 0.08 * Math.Cos(2 * Math.PI * distance / radius);
                coefficients[tap] = sinc * window;
                sum += coefficients[tap];
            }
            for (var tap = 0; tap < coefficients.Length; tap++)
            {
                coefficients[tap] /= sum;
            }
            filters[phase] = coefficients;
        }
        for (var index = 0; index < output.Length / 2; index++)
        {
            var numerator = index * 4;
            var center = numerator / 3;
            var filter = filters[numerator % 3];
            var sample = 0.0;
            for (var tap = 0; tap < filter.Length; tap++)
            {
                var input = Math.Clamp(center + tap - radius + 1, 0, sampleCount - 1);
                sample += BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(input * 2, 2)) * filter[tap];
            }
            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(index * 2, 2),
                (short)Math.Clamp(Math.Round(sample), short.MinValue, short.MaxValue));
        }
        return output;
    }
}
