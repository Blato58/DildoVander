using Kufr.Backend.Features.Access;

namespace Kufr.Backend.Features.Realtime;

internal static class RealtimeEndpoints
{
    internal static IEndpointRouteBuilder MapRealtimeTurns(this IEndpointRouteBuilder routes)
    {
        // Older PTT clients must upgrade to the session protocol. Reconstructing
        // isolated provider calls cannot preserve conversation/cancellation state.
        routes.MapPost("/v1/conversations/{id}/realtime-turns", (HttpContext context) =>
            ApiProblem.Create(context, 410, "conversation_engine_upgrade_required"));
        return routes;
    }
}
