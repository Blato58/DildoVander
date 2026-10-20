using Kufr.Backend.Features.Access;
using System.Text.Json.Serialization;

namespace Kufr.Backend.Features.Conversations;

internal static class ServerModeEndpoints
{
    public static IEndpointRouteBuilder MapServerModes(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/admin/modes", (
            HttpContext context,
            ServerAdminAccess access,
            ConversationStore conversations) =>
        {
            return access.Authenticate(context)
                ? Results.Ok(conversations.ReadServerModes())
                : ApiProblem.Create(context, 401, "admin_credential_required");
        });

        endpoints.MapPut("/v1/admin/modes", (
            HttpContext context,
            ServerModeChange request,
            ServerAdminAccess access,
            ConversationStore conversations) =>
        {
            if (!access.Authenticate(context))
            {
                return ApiProblem.Create(context, 401, "admin_credential_required");
            }

            var result = conversations.ChangeServerModes(request);
            return result.Error is null
                ? Results.Ok(result.State)
                : ApiProblem.Create(context, result.Status, result.Error);
        });
        return endpoints;
    }
}

internal sealed record ServerModeChange(string ExpectedRevision, string? DialogMode = null, string? VoiceMode = null, string? PersonalityId = null,
    bool? CharacterEnabled = null, string? CharacterTone = null, string? CharacterFinaleProfile = null, bool ResetCharacterVariety = false);
internal sealed record ServerModes(
    string Revision,
    [property: JsonIgnore] string DialogMode,
    [property: JsonIgnore] string VoiceMode,
    [property: JsonIgnore] bool DialogAvailable,
    [property: JsonIgnore] bool VoiceAvailable,
    int ActiveConversations,
    string PersonalityId,
    IReadOnlyList<PersonalityInfo> Personalities,
    string PersonalityLifetime = "persistent",
    string Lifetime = "until_restart",
    bool CharacterEnabled = false,
    string CharacterTone = "Adult",
    string CharacterFinaleProfile = "Blocked",
    string CharacterVersion = "dv01-v1",
    ConversationEngineCapabilities? Engines = null);
internal sealed record ConversationEngineCapabilities(int SchemaVersion, string PrimaryEngine, IReadOnlyList<ConversationEngineCapability> Engines);
internal sealed record ConversationEngineCapability(string Engine, string Model, bool Ready, string? UnavailableReason,
    IReadOnlyList<string> Voices, ConversationEngineSpeed Speed, IReadOnlyList<string> InputModes, IReadOnlyList<string> CameraSources);
internal sealed record ConversationEngineSpeed(bool Supported, double Min, double Max, double Default);
internal sealed record ServerModeResult(int Status, ServerModes? State, string? Error);
