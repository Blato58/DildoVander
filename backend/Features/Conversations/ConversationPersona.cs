using System.Security.Cryptography;
using System.Text;
using Kufr.Backend.Features.GptLive;

namespace Kufr.Backend.Features.Conversations;

internal sealed partial class ConversationStore
{
    private LivePromptSnapshot CreatePersonaSnapshot(string personalityId)
    {
        var baseline = LivePromptSnapshot.Create(personalityId,
            (liveOptions ?? new()) with { Character = characterPolicy });
        var published = personaLab?.PublishedOverride();
        if (published is null)
        {
            return baseline;
        }
        // Pin publication for this conversation. Drafts and later rollbacks must
        // never change an in-flight actor or its character/safety policy version.
        return baseline with
        {
            Instructions = published.Instructions,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(published.Instructions))),
            PersonaRevisionId = published.Id
        };
    }
}
