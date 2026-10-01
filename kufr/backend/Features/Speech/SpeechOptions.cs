namespace Kufr.Backend.Features.Speech;

internal readonly record struct SpeechOptions(string Voice, double Speed)
{
    internal static SpeechOptions Default => new("marin", 1.0);

    internal static bool IsValid(string? voice, double? speed)
    {
        return (voice is null or "cedar" or "marin" or "onyx" or "coral")
            && (speed is null || double.IsFinite(speed.Value)
                && speed.Value is >= 0.85 and <= 1.25);
    }

    internal static SpeechOptions Resolve(string? voice, double? speed) =>
        new(voice ?? Default.Voice, speed ?? Default.Speed);
}
