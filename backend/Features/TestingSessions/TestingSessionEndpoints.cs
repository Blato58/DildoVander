using System.Security.Claims;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.PersonaLab;
using Kufr.Backend.Features.Show;
using Kufr.Backend.Features.Wands;

namespace Kufr.Backend.Features.TestingSessions;

internal sealed record TestingPhotoRequest(string DataBase64,string MimeType,bool Consent=false);
internal sealed record TestingPortraitRequest(string UploadId,string EditionId);
internal static class TestingSessionEndpoints
{
    internal static IServiceCollection AddTestingSessions(this IServiceCollection services)
    {
        services.AddSingleton<TestingResources>();
        services.AddSingleton<TestingHardwareAdapter>();
        services.AddSingleton<TestingSessionService>();
        services.AddSingleton<ITestingConversationBridge,TestingConversationBridge>();
        return services;
    }
    internal static void MapTestingSessions(this WebApplication app)
    {
        if(!app.Configuration.GetValue<bool>("ApplicationIdentity:Enabled"))return;
        var group=app.MapGroup("/app/v1/testing-sessions").RequireAuthorization().RequireRateLimiting("account");
        group.AddEndpointFilter(async (invocation,next)=>
        {
            var http=invocation.HttpContext;
            if(!http.User.IsInRole(AccountRoles.Tester))return ApiProblem.Create(http,403,"tester_role_required");
            if(http.User.FindFirstValue(ClaimTypes.NameIdentifier) is not {Length:>0})return ApiProblem.Create(http,401,"account_required");
            try{return await next(invocation);}
            catch(TestingException e){return ApiProblem.Create(http,e.Status,e.Code);}
            catch(CharacterException e){return ApiProblem.Create(http,e.Status,e.Code);}
            catch(WandGenerationException e){return ApiProblem.Create(http,e.Status,e.Code);}
            catch(LabException e){return ApiProblem.Create(http,e.Status,e.Code);}
            catch(KeepsakeException e){return ApiProblem.Create(http,e.Status,e.Code);}
            catch(DuelException e){return ApiProblem.Create(http,e.Status,e.Code);}
            catch(JsonException){return ApiProblem.Create(http,400,"invalid_test_request");}
            catch(FormatException){return ApiProblem.Create(http,400,"invalid_test_request");}
            catch(OperationCanceledException){return ApiProblem.Create(http,408,"test_operation_cancelled");}
        });
        group.MapGet("/catalog",(PersonaLabRuntime runtime)=>Results.Json(new
        {
            engines=new[]{"gpt-live-1","gpt-realtime-2.1"},
            capabilities=new {text=true,voice=true,hybrid=true,virtualHardware=true,providerConfigured=new {live=runtime.LiveAvailable,realtime=runtime.RealtimeAvailable}},
            evidence="Configuration availability is not a provider handshake. Provider events confirm execution. Hardware observations are always simulated."
        }));
        group.MapGet("",(HttpContext h,TestingSessionService s)=>Results.Json(s.Sessions(Owner(h))));
        group.MapPost("",(HttpContext h,TestingSessionRequest r,TestingSessionService s)=>Results.Json(s.Create(Owner(h),r)));
        group.MapGet("/characters",(HttpContext h,TestingSessionService s)=>Results.Json(s.Characters(Owner(h))));
        group.MapPost("/characters",async (HttpContext h,TestingCharacterRequest r,TestingSessionService s)=>Results.Json(await s.SaveCharacterAsync(Owner(h),null,r,h.RequestAborted)));
        group.MapPut("/characters/{id}",async (HttpContext h,string id,TestingCharacterRequest r,TestingSessionService s)=>Results.Json(await s.SaveCharacterAsync(Owner(h),id,r,h.RequestAborted)));
        group.MapDelete("/characters/{id}",async(HttpContext h,string id,TestingSessionService s)=>{await s.DeleteCharacterAsync(Owner(h),id,h.RequestAborted);return Results.NoContent();});
        group.MapPost("/characters/{id}/enrich",async (HttpContext h,string id,TestingEnrichRequest r,TestingSessionService s)=>Results.Json(await s.EnrichAsync(Owner(h),id,r.OpenAiConsent,h.RequestAborted)));
        group.MapGet("/{id}",async(HttpContext h,string id,TestingSessionService s)=>Results.Json(await s.DetailAsync(Owner(h),id,h.RequestAborted)));
        group.MapPost("/{id}/character",async(HttpContext h,string id,TestingCharacterSelection r,TestingSessionService s)=>Results.Json(await s.SelectCharacterAsync(Owner(h),id,r,h.RequestAborted)));
        group.MapPut("/{id}/mode",(HttpContext h,string id,TestingModeRequest r,TestingSessionService s)=>Results.Json(s.Mode(Owner(h),id,r)));
        group.MapPost("/{id}/end",async(HttpContext h,string id,TestingSessionService s)=>Results.Json(await s.EndAsync(Owner(h),id,h.RequestAborted)));
        group.MapPost("/{id}/cancel",async(HttpContext h,string id,TestingSessionService s)=>{await s.CancelAsync(Owner(h),id,h.RequestAborted);return Results.NoContent();});
        group.MapDelete("/{id}",async(HttpContext h,string id,TestingSessionService s)=>{await s.DeleteAsync(Owner(h),id,h.RequestAborted);return Results.NoContent();});
        group.MapPost("/{id}/select",async(HttpContext h,string id,TestingSessionService s)=>Results.Json(await s.SelectAsync(Owner(h),id,h.RequestAborted)));
        group.MapPost("/{id}/generate",async(HttpContext h,string id,TestingSessionService s)=>Results.Json(await s.GenerateAsync(Owner(h),id,h.RequestAborted)));
        group.MapPost("/{id}/retry-generation",async(HttpContext h,string id,TestingGenerationRetryRequest r,TestingSessionService s)=>Results.Json(await s.RetryGenerationAsync(Owner(h),id,r,h.RequestAborted)));
        group.MapPost("/{id}/fault",async(HttpContext h,string id,TestingFaultRequest r,TestingSessionService s)=>Results.Json(await s.FaultAsync(Owner(h),id,r.Fault,h.RequestAborted)));
        group.MapGet("/{id}/duels",async(HttpContext h,string id,TestingSessionService s)=>Results.Json(await s.DuelsAsync(Owner(h),id,h.RequestAborted)));
        group.MapPost("/{id}/duels",(HttpContext h,string id,TestingDuelRequest r,TestingSessionService s)=>Results.Json(s.CreateDuel(Owner(h),id,r)));
        group.MapGet("/{id}/duels/{duelId}",async(HttpContext h,string id,string duelId,TestingSessionService s)=>Results.Json(await s.ReadDuelAsync(Owner(h),id,duelId,h.RequestAborted)));
        group.MapPost("/{id}/duels/{duelId}/start",(HttpContext h,string id,string duelId,DuelCommand r,TestingSessionService s)=>Results.Json(s.StartDuel(Owner(h),id,duelId,r)));
        group.MapPost("/{id}/duels/{duelId}/move",async(HttpContext h,string id,string duelId,DuelChoiceRequest r,TestingSessionService s)=>Results.Json(await s.DuelMoveAsync(Owner(h),id,duelId,r,h.RequestAborted)));
        group.MapPost("/{id}/duels/{duelId}/advance",async(HttpContext h,string id,string duelId,TestingSessionService s)=>Results.Json(await s.AdvanceDuelAsync(Owner(h),id,duelId,h.RequestAborted)));
        group.MapPost("/{id}/duels/{duelId}/narrate",async(HttpContext h,string id,string duelId,TestingSessionService s)=>Results.Json(await s.NarrateDuelAsync(Owner(h),id,duelId,h.RequestAborted)));
        group.MapGet("/{id}/keepsakes/{keepsakeId}/assets/{assetId}",(HttpContext h,string id,string keepsakeId,string assetId,TestingSessionService s)=>
        {var asset=s.Asset(Owner(h),id,keepsakeId,assetId);h.Response.Headers.CacheControl="no-store";return Results.File(asset.Bytes,asset.Asset.MediaType);});
        group.MapPost("/{id}/keepsakes/{keepsakeId}/photo",async(HttpContext h,string id,string keepsakeId,TestingPhotoRequest r,TestingSessionService s)=>
        {
            if(!r.Consent||r.DataBase64.Length>90_000||r.MimeType is not("image/jpeg" or "image/png"))return ApiProblem.Create(h,400,"test_photo_consent_or_bounds");
            var bytes=Convert.FromBase64String(r.DataBase64);using var stream=new MemoryStream(bytes);
            return Results.Json(await s.PhotoAsync(Owner(h),id,keepsakeId,stream,r.MimeType,r.Consent,h.RequestAborted));
        });
        group.MapPost("/{id}/portrait",async(HttpContext h,string id,TestingPortraitRequest r,TestingSessionService s)=>Results.Json(await s.PortraitAsync(Owner(h),id,r.UploadId,r.EditionId,h.RequestAborted)));
        group.MapGet("/{id}/diagnostics",async(HttpContext h,string id,TestingSessionService s)=>
        {
            var detail=await s.DetailAsync(Owner(h),id,h.RequestAborted);h.Response.Headers.CacheControl="no-store";
            return Results.Json(new {testSessionId=id,scope="isolated_testing",detail.Session.Engine,detail.Session.InputMode,
                detail.Session.Status,providerEvents=detail.Conversation.Events,virtualHardwareEvents=detail.Hardware.Events});
        });
    }
    private static string Owner(HttpContext h)=>h.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
}
