using Kufr.Backend.Features.Character;
using Kufr.Backend.Features.Customers;

namespace Kufr.Backend.Features.Conversations;

internal sealed record GuestToneDirective(long Revision, CharacterTone Tone, bool RoastAllowed, string Instructions);

// Mutable only through a current-call binding under ConversationStore.gate.
// This state belongs to the conversation, not its replaceable provider socket.
internal sealed class ConversationGuestToneState
{
    private readonly CharacterToneState state;
    private readonly CustomerConversationPreferenceSnapshot preferences;
    private readonly Dictionary<string, long> accepted = new(StringComparer.Ordinal);
    private long revision;

    internal ConversationGuestToneState(CharacterPolicy host, CustomerConversationPreferenceSnapshot preferences)
    {
        this.preferences = preferences;
        state = new(CustomerConversationPreferencePolicy.Restrict(host, preferences).HostTone);
    }

    internal GuestToneDirective Read() => new(revision, state.Tone, state.RoastAllowed,
        CustomerConversationPreferencePolicy.TrustedInstructions(preferences)
        + $" Effective frozen host/guest ceiling and current tone: {state.Tone}."
        + ((int)state.Tone <= (int)CharacterTone.Dry ? " Do not swear." : "")
        + (state.RoastAllowed ? "" : " The guest has withdrawn roast: no personal insults, teasing, or roast callbacks."));

    internal bool Apply(CharacterSignalKind kind, string source, long sourceRevision)
    {
        if (!IsToneControl(kind)) { return false; }
        if (string.IsNullOrWhiteSpace(source) || source.Length > 128
            || sourceRevision is < 0 or > CharacterSession.MaximumSourceRevision)
        { throw new InvalidDataException("Invalid guest tone source."); }
        if (accepted.TryGetValue(source, out var previous) && sourceRevision <= previous) { return false; }
        if (!accepted.ContainsKey(source) && accepted.Count >= 128)
        { throw new InvalidDataException("Guest tone input capacity reached."); }
        if (revision == long.MaxValue) { throw new InvalidDataException("Guest tone revision exhausted."); }
        accepted[source] = sourceRevision;
        revision++;
        return state.ApplyControl(kind, source, sourceRevision);
    }

    internal void SetOperatorTone(CharacterTone requested)
    {
        if (revision == long.MaxValue) { throw new InvalidDataException("Guest tone revision exhausted."); }
        state.SetOperatorTone(requested); revision++;
    }

    internal static bool IsToneControl(CharacterSignalKind kind) => kind is CharacterSignalKind.ToneDown
        or CharacterSignalKind.ToneUp or CharacterSignalKind.NoSwearing or CharacterSignalKind.WithdrawRoast;
}

// A typed trusted hook, never serialized or supplied by the device/phone.
internal sealed class GuestToneBinding(Func<GuestToneDirective> read,
    Func<CharacterSignalKind, string, long, bool> apply, Action<CharacterTone> setTone) : ICharacterToneState
{
    internal GuestToneDirective Read() => read();
    public CharacterTone Tone => Read().Tone;
    public bool RoastAllowed => Read().RoastAllowed;
    public void SetOperatorTone(CharacterTone requested) => setTone(requested);
    public bool ApplyControl(CharacterSignalKind kind, string sourceId, long revision) => apply(kind, sourceId, revision);
    internal GuestToneDirective AcceptInput(string source, long revision, string text)
    {
        if (CharacterSession.ClassifyExplicitControl(text) is { } control
            && ConversationGuestToneState.IsToneControl(control)) { ApplyControl(control, source, revision); }
        return Read();
    }
}

internal sealed partial class ConversationStore
{
    private GuestToneBinding BindGuestTone(RealtimeCall call, ConversationGuestToneState owner)
    {
        void Validate()
        {
            // No cleanup or memory/account/profile access here. Outer admission
            // owns those fences; this callback only checks its already-held call.
            if (call.Cancellation.IsCancellationRequested
                || !activePaidCalls.TryGetValue(call.OperationId, out var paid)
                || !ReferenceEquals(paid.Cancellation, call.Cancellation)
                || !conversations.TryGetValue(call.Correlation.ConversationId, out var entry)
                || !ReferenceEquals(entry.GuestTone, owner)
                || entry.DeviceId != call.Correlation.DeviceId || entry.BootId != call.Correlation.BootId
                || entry.LiveGeneration != call.SessionGeneration
                || !OperationEpoch.Same(entry.OperationEpoch, call.Correlation.OperationEpoch)
                || clock.GetUtcNow() >= entry.ExpiresAt)
            { throw new OperationCanceledException(call.Cancellation.Token); }
        }
        return new(() => { lock (gate) { Validate(); return owner.Read(); } },
            (kind, source, revision) => { lock (gate) { Validate(); return owner.Apply(kind, source, revision); } },
            requested => { lock (gate) { Validate(); owner.SetOperatorTone(requested); } });
    }
}
