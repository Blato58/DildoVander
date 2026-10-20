using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Identity;

namespace Kufr.Backend.Features.ModelReleases;

internal static class ModelReleaseEndpoints
{
    public static IServiceCollection AddModelReleases(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => new ModelReleaseStore(configuration));
        return services;
    }

    public static void MapModelReleases(this WebApplication app)
    {
        var group = app.MapGroup("/app/v1/admin/model-releases").RequireAuthorization().RequireRateLimiting("account");
        group.MapGet("", async (HttpContext context, ModelReleaseStore store) =>
        {
            if (!AccountRoles.IsAdmin(context.User)) { return ApiProblem.Create(context, 403, "account_admin_required"); }
            context.Response.Headers.CacheControl = "no-store";
            try { return Results.Ok(await store.ListAsync(context.RequestAborted)); }
            catch (ModelReleaseException error) { return Problem(context, error.Code); }
            catch (IOException) { return ApiProblem.Create(context, 503, "model_store_unavailable"); }
            catch (UnauthorizedAccessException) { return ApiProblem.Create(context, 503, "model_store_unavailable"); }
        });
        group.MapPost("/active", async (SelectModelRelease request, HttpContext context, ModelReleaseStore store) =>
        {
            if (!AccountRoles.IsAdmin(context.User)) { return ApiProblem.Create(context, 403, "account_admin_required"); }
            if (await AccountAuthorization.RequireRecentAsync(context) is { } denied) { return denied; }
            context.Response.Headers.CacheControl = "no-store";
            try { return Results.Ok(new { active = await store.SelectAsync(request, context.RequestAborted) }); }
            catch (ModelReleaseException error) { return Problem(context, error.Code); }
            catch (IOException) { return ApiProblem.Create(context, 503, "model_store_unavailable"); }
            catch (UnauthorizedAccessException) { return ApiProblem.Create(context, 503, "model_store_unavailable"); }
        });
    }
    private static IResult Problem(HttpContext context, string code) => ApiProblem.Create(context,
        code switch
        {
            "model_activation_busy" or "active_model_changed" => 409,
            "model_releases_disabled" or "model_store_unavailable" => 503,
            "verified_model_required" => 422,
            _ => 400
        }, code);
}
