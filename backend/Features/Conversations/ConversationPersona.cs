using System.Security.Cryptography;
using System.Text;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Customers;

namespace Kufr.Backend.Features.Conversations;

internal sealed partial class ConversationStore
{
    private LivePromptSnapshot CreatePersonaSnapshot(string personalityId,
        CustomerConversationPreferenceSnapshot? preferences = null, string language = "cs-CZ")
    {
        var baseline = LivePromptSnapshot.Create(personalityId,
            (liveOptions ?? new()) with { Character = characterPolicy });
        var published = personaLab?.PublishedOverride();
        // Pin publication for this conversation. Drafts and later rollbacks must
        // never change an in-flight actor or its character/safety policy version.
        var pinned = published is null ? baseline : baseline with
        {
            Instructions = published.Instructions,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(published.Instructions))),
            PersonaRevisionId = published.Id
        };
        return pinned.WithConversationPreferences(preferences, language);
    }
}
