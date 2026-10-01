using System.Buffers.Binary;

namespace Kufr.Backend.Features.Speech;

internal static class PcmSpeechAudio
{
    internal const int MaxProviderBytes = 24_000 * 2 * 30;
    internal const int MaxWavBytes = 44 + 32_000 * 2 * 30;
    private const int FilterRadius = 16;

    public static byte[] ToDeviceWav(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length == 0 || pcm.Length % 2 != 0 || pcm.Length > MaxProviderBytes)
        {
            throw new InvalidDataException("Invalid provider PCM length.");
        }

        var inputSamples = pcm.Length / 2;
        var outputSamples = (inputSamples * 4 + 2) / 3;
        var wav = new byte[44 + outputSamples * 2];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), 32_000);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), 64_000);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), outputSamples * 2);

        // Four fractional phases reconstruct the 24 kHz signal with a windowed
        // sinc low-pass filter. Relabeling or repeating samples changes pitch
        // or creates spectral images; neither is a sample-rate conversion.
        var filters = BuildFilters();
        for (var output = 0; output < outputSamples; output++)
        {
            var positionNumerator = output * 3;
            var center = positionNumerator / 4;
            var filter = filters[positionNumerator % 4];
            var value = 0.0;
            for (var tap = 0; tap < filter.Length; tap++)
            {
                var input = Math.Clamp(center + tap - FilterRadius + 1, 0, inputSamples - 1);
                value += BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(input * 2, 2)) * filter[tap];
            }

            var sample = (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + output * 2, 2), sample);
        }

        return wav;
    }

    private static double[][] BuildFilters()
    {
        var filters = new double[4][];
        for (var phase = 0; phase < filters.Length; phase++)
        {
            var coefficients = new double[FilterRadius * 2];
            var sum = 0.0;
            for (var tap = 0; tap < coefficients.Length; tap++)
            {
                var distance = tap - FilterRadius + 1 - phase / 4.0;
                var sinc = Math.Abs(distance) < 1e-12
                    ? 1.0
                    : Math.Sin(Math.PI * distance) / (Math.PI * distance);
                var window = 0.42
                    + 0.5 * Math.Cos(Math.PI * distance / FilterRadius)
                    + 0.08 * Math.Cos(2 * Math.PI * distance / FilterRadius);
                coefficients[tap] = sinc * window;
                sum += coefficients[tap];
            }

            for (var tap = 0; tap < coefficients.Length; tap++)
            {
                coefficients[tap] /= sum;
            }

            filters[phase] = coefficients;
        }

        return filters;
    }
}
