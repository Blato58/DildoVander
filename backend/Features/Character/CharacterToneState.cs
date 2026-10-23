namespace Kufr.Backend.Features.Character;

// Character direction and guest preferences share these transitions. Feature
// enablement, evidence, scenes and physical controls remain separate owners.
internal interface ICharacterToneState
{
    CharacterTone Tone { get; }
    bool RoastAllowed { get; }
    void SetOperatorTone(CharacterTone requested);
    bool ApplyControl(CharacterSignalKind kind, string sourceId, long revision);
}

internal sealed class CharacterToneState : ICharacterToneState
{
    private readonly CharacterTone ceiling;
    private int limit;

    internal CharacterToneState(CharacterTone ceiling)
    {
        this.ceiling = ceiling;
        limit = (int)ceiling;
        Tone = ceiling;
    }

    public CharacterTone Tone { get; private set; }
    public bool RoastAllowed { get; private set; } = true;

    public void SetOperatorTone(CharacterTone requested)
        => Tone = (CharacterTone)Math.Min((int)requested, Math.Min((int)ceiling, limit));

    public bool ApplyControl(CharacterSignalKind kind, string sourceId, long revision)
    {
        switch (kind)
        {
            case CharacterSignalKind.ToneDown:
                Tone = (CharacterTone)Math.Max(0, (int)Tone - 1);
                return true;
            case CharacterSignalKind.NoSwearing:
                limit = Math.Min(limit, (int)CharacterTone.Dry);
                Tone = (CharacterTone)Math.Min((int)Tone, limit);
                return true;
            case CharacterSignalKind.ToneUp:
                SetOperatorTone((CharacterTone)Math.Min((int)CharacterTone.Chaos, (int)Tone + 1));
                return false;
            case CharacterSignalKind.WithdrawRoast:
                RoastAllowed = false;
                return true;
            default:
                return false;
        }
    }
}
