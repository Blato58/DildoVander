using System.Text.Json;
using System.Text.Json.Serialization;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.CustomerData;
using Kufr.Backend.Features.Identity;

namespace Kufr.Backend.Features.Customers;

internal static class CustomerConversationPreferenceEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static void MapCustomerConversationPreferences(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<AccountConfiguration>().Enabled) { return; }
        var group = app.MapGroup("/app/v1/customer/conversation-preferences")
            .RequireAuthorization().RequireRateLimiting("account");
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var http = invocation.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                if (http.Request.Headers.ContainsKey("Authorization"))
                { return ApiProblem.Create(http, 401, "account_required"); }
                if (http.Request.Query.Count != 0)
                { return ApiProblem.Create(http, 400, "conversation_preferences_invalid"); }
                return await next(invocation);
            }
            catch (CharacterException error) { return ApiProblem.Create(http, error.Status, error.Code); }
            catch (CustomerDataAccessException error) { return ApiProblem.Create(http, error.Status, error.Code); }
            catch (JsonException) { return ApiProblem.Create(http, 400, "conversation_preferences_invalid"); }
        });
        group.MapGet("", (HttpContext http, CustomerAdmissionAuthority authority) =>
        {
            authority.ValidateDatabasePaths();
            using var guard = authority.AcquireCookie(http, token: http.RequestAborted);
            return Results.Ok(guard.ReadConversationPreferences());
        });
        group.MapPut("", async (HttpContext http, CustomerAdmissionAuthority authority) =>
        {
            var request = await ReadAsync(http);
            if ((!Guid.TryParseExact(request.ProfileId, "D", out var profile)
                && !Guid.TryParseExact(request.ProfileId, "N", out profile)) || profile == Guid.Empty)
            { throw new CharacterException("conversation_preferences_invalid"); }
            authority.ValidateDatabasePaths();
            using var guard = authority.AcquireCookie(http, token: http.RequestAborted);
            return Results.Ok(guard.WriteConversationPreferences(request));
        });
    }

    private static async Task<CustomerConversationPreferenceWrite> ReadAsync(HttpContext http)
    {
        using var buffer = new MemoryStream();
        var bytes = new byte[1024];
        while (true)
        {
            var count = await http.Request.Body.ReadAsync(bytes, http.RequestAborted);
            if (count == 0) { break; }
            if (buffer.Length + count > 2048)
            { throw new CharacterException("request_too_large", 413); }
            buffer.Write(bytes, 0, count);
        }
        return JsonSerializer.Deserialize<CustomerConversationPreferenceWrite>(buffer.ToArray(), Json)
            ?? throw new JsonException();
    }
}
