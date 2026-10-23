using Kufr.Backend.Features.Customers;

namespace Kufr.Backend.Features.Identity;

internal sealed partial class CustomerAdmissionGuard
{
    internal CustomerConversationPreferenceView? ReadExportConversationPreferences()
    {
        ValidateCurrent();
        var value = profiles.ReadHeldExportConversationPreferences(character, Proof.AccountId);
        ValidateCurrent();
        return value;
    }

    internal CustomerConversationPreferenceView ReadConversationPreferences()
    {
        ValidateCurrent();
        var profile = profiles.ReadHeldCurrentAdmissionProfile(character, Proof.AccountId);
        var value = profiles.ReadHeldConversationPreferences(character, Proof.AccountId, profile);
        ValidateCurrent();
        return value;
    }

    internal CustomerConversationPreferenceSnapshot CaptureConversationPreferences(string profileId, long revision)
    {
        var profile = ReadOwnedProfile(profileId, revision);
        var value = profiles.ReadHeldConversationPreferences(character, Proof.AccountId, profile);
        ValidateCurrent();
        return new(1, value.PreferenceRevision, value.ConversationLanguage, value.Tone);
    }

    // This dedicated mutation completes and closes the guard. Every other
    // admission/export use remains read-only and rolls its reservations back.
    internal CustomerConversationPreferenceView WriteConversationPreferences(CustomerConversationPreferenceWrite request)
    {
        if (request.SchemaVersion != 1 || request.ProfileRevision < 1 || request.ExpectedPreferenceRevision < 0
            || !CustomerConversationPreferencePolicy.IsValid(request.ConversationLanguage, request.Tone))
        { throw new CharacterException("conversation_preferences_invalid"); }
        var profile = ReadOwnedProfile(request.ProfileId, request.ProfileRevision);
        var value = profiles.WriteHeldConversationPreferences(character, Proof.AccountId, profile, request);
        ValidateCurrent();
        character.Transaction.Commit();
        Dispose();
        return value;
    }
}
