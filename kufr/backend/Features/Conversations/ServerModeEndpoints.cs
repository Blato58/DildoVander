using Kufr.Backend.Features.Access;

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

internal sealed record ServerModeChange(string ExpectedRevision, string DialogMode, string VoiceMode, string? PersonalityId = null);
internal sealed record ServerModes(
    string Revision,
    string DialogMode,
    string VoiceMode,
    bool DialogAvailable,
    bool VoiceAvailable,
    int ActiveConversations,
    string PersonalityId,
    IReadOnlyList<PersonalityInfo> Personalities,
    string PersonalityLifetime = "persistent",
    string Lifetime = "until_restart");
internal sealed record ServerModeResult(int Status, ServerModes? State, string? Error);
