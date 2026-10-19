using Kufr.Backend.Features.Character;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Realtime;

namespace Kufr.Backend.Features.Conversations;

internal sealed partial class ConversationStore
{
    private string modeRevision = Guid.NewGuid().ToString();
    private string personalityId = journal.ReadPersonality();

    public ServerModes ReadServerModes()
    {
        lock (gate)
        {
            CleanupExpired(clock.GetUtcNow());
            return ServerModesSnapshot();
        }
    }

    public ServerModeResult ChangeServerModes(ServerModeChange request)
    {
        lock (gate)
        {
            CleanupExpired(clock.GetUtcNow());
            if (request.DialogMode is not null || request.VoiceMode is not null)
            {
                return new(410, null, "conversation_engine_upgrade_required");
            }
            var requestedPersonality = request.PersonalityId ?? personalityId;
            if (!PersonalityCatalog.Contains(requestedPersonality))
                return new(400, null, "invalid_personality");

            if (request.ExpectedRevision != modeRevision)
            {
                return new(409, null, "mode_revision_mismatch");
            }

            var nextCharacter = characterPolicy;
            if (request.CharacterEnabled.HasValue || request.CharacterTone is not null || request.CharacterFinaleProfile is not null)
            {
                try
                {
                    nextCharacter = CharacterConfiguration.Read(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Enabled"] = (request.CharacterEnabled ?? characterPolicy.Enabled).ToString(),
                        ["HostTone"] = request.CharacterTone ?? characterPolicy.HostTone.ToString(),
                        ["FinaleProfile"] = request.CharacterFinaleProfile ?? characterPolicy.FinaleProfile.ToString()
                    }).Build());
                }
                catch (InvalidOperationException) { return new(400, null, "invalid_character_policy"); }
            }
            if (nextCharacter.Enabled && (liveOptions?.Mode != LiveActorMode.AutonomousActor || requestedPersonality != PersonalityCatalog.OllivanderId))
            {
                return new(409, null, "character_requires_autonomous_ollivander");
            }

            var current = ServerModesSnapshot();
            if (requestedPersonality == personalityId && nextCharacter == characterPolicy && !request.ResetCharacterVariety)
            {
                return new(200, current, null);
            }

            // Create and all provider admission share this lock. Mode changes
            // cannot split a conversation or race a still-unwinding paid call.
            var modesChanged = requestedPersonality != personalityId;
            if (modesChanged && (conversations.Count != 0 || activePaidCalls.Count != 0))
            {
                return new(409, null, "conversations_active");
            }

            // Commit persistence before publishing the in-memory configuration.
            if (requestedPersonality != personalityId) journal.WritePersonality(requestedPersonality);
            personalityId = requestedPersonality;
            characterPolicy = nextCharacter;
            if (request.ResetCharacterVariety) { characterStyles.Clear(); }
            modeRevision = Guid.NewGuid().ToString();
            return new(200, ServerModesSnapshot(), null);
        }
    }

    private ServerModes ServerModesSnapshot() => new(
        modeRevision,
        "retired",
        "retired",
        false,
        false,
        conversations.Count,
        personalityId,
        PersonalityCatalog.Inventory,
        CharacterEnabled: characterPolicy.Enabled,
        CharacterTone: characterPolicy.HostTone.ToString(),
        CharacterFinaleProfile: characterPolicy.FinaleProfile.ToString(),
        Engines: EngineCapabilities());

    private ConversationEngineCapabilities EngineCapabilities()
    {
        var ready = !string.IsNullOrWhiteSpace(configuration?["OPENAI_API_KEY"]);
        var unavailableReason = ready ? null : "provider_credential_missing";
        string[] voices = ["cedar", "marin", "coral"];
        string[] inputs = ["audio"];
        string[] cameras = ["phone", "suitcase"];
        return new(1, "gpt-live", [
            new("gpt-live", GptLiveProtocol.Model, ready, unavailableReason, voices,
                new(false, 1, 1, 1), inputs, cameras),
            new("realtime", RealtimeProtocol.Model, ready, unavailableReason, voices,
                new(true, 0.8, 1.2, 1), inputs, cameras)
        ]);
    }
}
