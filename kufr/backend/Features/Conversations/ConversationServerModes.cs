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
            if (request.DialogMode is not ("fake" or "responses")
                || request.VoiceMode is not ("fake" or "openai"))
            {
                return new(400, null, "invalid_mode");
            }
            var requestedPersonality = request.PersonalityId ?? personalityId;
            if (!PersonalityCatalog.Contains(requestedPersonality))
                return new(400, null, "invalid_personality");

            if (request.ExpectedRevision != modeRevision)
            {
                return new(409, null, "mode_revision_mismatch");
            }

            var current = ServerModesSnapshot();
            if (request.DialogMode == current.DialogMode && request.VoiceMode == current.VoiceMode
                && requestedPersonality == personalityId)
            {
                return new(200, current, null);
            }

            // Create and all provider admission share this lock. Mode changes
            // cannot split a conversation or race a still-unwinding paid call.
            if (conversations.Count != 0 || activePaidCalls.Count != 0)
            {
                return new(409, null, "conversations_active");
            }

            if ((request.DialogMode == "responses" && dialogRunner?.Available != true)
                || (request.VoiceMode == "openai" && voiceRunner?.Available != true))
            {
                return new(409, null, "provider_unavailable");
            }

            // Commit persistence before publishing the in-memory configuration.
            if (requestedPersonality != personalityId) journal.WritePersonality(requestedPersonality);
            personalityId = requestedPersonality;
            dialogRunner?.SetEnabled(request.DialogMode == "responses");
            voiceRunner?.SetEnabled(request.VoiceMode == "openai");
            modeRevision = Guid.NewGuid().ToString();
            return new(200, ServerModesSnapshot(), null);
        }
    }

    private ServerModes ServerModesSnapshot() => new(
        modeRevision,
        dialogRunner?.Enabled == true ? "responses" : "fake",
        voiceRunner?.Enabled == true ? "openai" : "fake",
        dialogRunner?.Available == true,
        voiceRunner?.Available == true,
        conversations.Count,
        personalityId,
        PersonalityCatalog.Inventory);
}
