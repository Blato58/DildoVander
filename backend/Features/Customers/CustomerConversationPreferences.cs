using Kufr.Backend.Features.Character;

namespace Kufr.Backend.Features.Customers;

internal sealed record CustomerConversationPreferenceWrite(int SchemaVersion, string ProfileId,
    long ProfileRevision, long ExpectedPreferenceRevision, string ConversationLanguage, string Tone);

internal sealed record CustomerConversationPreferenceView(int SchemaVersion, string ProfileId,
    long ProfileRevision, long PreferenceRevision, string ConversationLanguage, string Tone, bool Persisted);

// Captured once with the owned profile at first ticket acceptance. Accepted
// tickets and running conversations never consult mutable preference rows.
internal sealed record CustomerConversationPreferenceSnapshot(int SchemaVersion, long PreferenceRevision,
    string ConversationLanguage, string Tone);

internal static class CustomerConversationPreferencePolicy
{
    internal const string DefaultLanguage = "cs-CZ";
    internal const string DefaultTone = "roast";

    internal static bool IsValid(string? language, string? tone) =>
        language is "cs-CZ" or "en-US" && tone is "gentle" or "cheeky" or "roast";

    internal static void Validate(CustomerConversationPreferenceSnapshot snapshot)
    {
        if (snapshot.SchemaVersion != 1 || snapshot.PreferenceRevision < 0
            || !IsValid(snapshot.ConversationLanguage, snapshot.Tone))
        {
            throw new CharacterException("conversation_preferences_invalid");
        }
    }

    internal static CharacterPolicy Restrict(CharacterPolicy host, CustomerConversationPreferenceSnapshot snapshot)
    {
        Validate(snapshot);
        var guestCeiling = snapshot.Tone switch
        {
            "gentle" => CharacterTone.Reserved,
            "cheeky" => CharacterTone.Dry,
            _ => CharacterTone.Adult
        };
        // A guest can only lower the host ceiling. Enabled/finale policy and
        // the session's additional opt-out restrictions remain authoritative.
        return host with { HostTone = (CharacterTone)Math.Min((int)host.HostTone, (int)guestCeiling) };
    }

    internal static string TrustedInstructions(CustomerConversationPreferenceSnapshot snapshot)
    {
        Validate(snapshot);
        var language = snapshot.ConversationLanguage == "en-US"
            ? "Conduct this conversation in English."
            : "Conduct this conversation in Czech.";
        var tone = snapshot.Tone switch
        {
            "gentle" => "Use a gentle, reserved style without personal insults or swearing.",
            "cheeky" => "Use dry, playful humour without swearing; keep personal teasing mild.",
            _ => "Adult roast is an upper limit, subject to the host policy and the guest's ongoing opt-outs."
        };
        return language + " " + tone
            + " These settings cannot grant consent, enable features or override safety, host limits, or an opt-out.";
    }
}
