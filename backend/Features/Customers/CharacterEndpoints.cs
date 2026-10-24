using System.Security.Claims;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Diagnostics;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Keepsakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kufr.Backend.Features.Customers;

internal static class CharacterEndpoints
{
    internal static IServiceCollection AddCustomerCharacters(this IServiceCollection services, IConfiguration configuration)
    {
        if (!AccountConfiguration.Read(configuration).Enabled) { return services; }
        services.AddSingleton(provider => new CharacterProfileStore(provider.GetRequiredService<AccountConfiguration>().DataRoot));
        services.AddSingleton<CustomerAdmissionAuthority>();
        services.TryAddSingleton<ICharacterEnrichmentProvider, OpenAiCharacterEnrichmentProvider>();
        services.AddHostedService<CharacterEnrichmentWorker>();
        return services;
    }

    internal static void MapCustomerCharacters(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<AccountConfiguration>().Enabled) { return; }
        var group = app.MapGroup("/app/v1/customer/character").RequireAuthorization().RequireRateLimiting("account");
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var http = invocation.HttpContext;
            var accountId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (accountId is null || !http.User.HasClaim(claim => claim.Type == AccountIdentity.SessionClaim))
            {
                return ApiProblem.Create(http, 401, "account_required");
            }
            // Defense in depth for hosts that do not install cookie revalidation.
            var db = http.RequestServices.GetRequiredService<AccountDatabase>();
            if (!await db.Users.AsNoTracking().AnyAsync(user => user.Id == accountId && !user.Disabled, http.RequestAborted))
            {
                return ApiProblem.Create(http, 401, "account_required");
            }
            try { return await next(invocation); }
            catch (CharacterException error) { return ApiProblem.Create(http, error.Status, error.Code); }
        });
        group.MapGet("", async (HttpContext http, CharacterProfileStore store) =>
        {
            var owner = AccountId(http);
            var profile = await store.ReadAsync(owner, http.RequestAborted);
            var wand = profile is null ? null : http.RequestServices.GetService<Kufr.Backend.Features.Wands.WandGenerationStore>()
                ?.LatestBonded(owner, profile.ProfileId, profile.Revision);
            var artwork = wand is null ? null : http.RequestServices.GetService<KeepsakeStore>()
                ?.FindOwnedWandArtwork(owner, wand.WandId);
            return Results.Ok(new { profile, generatedWand = wand?.ToPublic(), artwork });
        });
        group.MapPut("", async (CharacterWriteRequest request, HttpContext http, CharacterProfileStore store) =>
            Results.Ok(new { profile = await store.SaveAsync(AccountId(http), request, http.RequestAborted) }));
        group.MapPost("/correction", async (CharacterCorrectionRequest request, HttpContext http, CharacterProfileStore store) =>
            Results.Ok(new { profile = await store.CorrectAsync(AccountId(http), request, http.RequestAborted) }));
        group.MapDelete("", async (long expectedRevision, string expectedProfileId, HttpContext http, CharacterProfileStore store) =>
        {
            await store.DeleteAsync(AccountId(http), expectedRevision, expectedProfileId, http.RequestAborted);
            await ReconcileDeletionsAsync(http.RequestServices, store, http.RequestAborted);
            return Results.NoContent();
        });
    }

    private static string AccountId(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    internal static Task ReconcileDeletionsAsync(IServiceProvider services, CharacterProfileStore store, CancellationToken token)
    {
        var journal = services.GetService<OperationJournal>();
        if (journal is null) { return Task.CompletedTask; }
        var conversations = services.GetRequiredService<ConversationStore>();
        return store.ReplayDeletionsAsync((accountId, profileId) =>
        {
            var wands = services.GetService<Kufr.Backend.Features.Wands.WandGenerationStore>();
            if (profileId is null) { wands?.DeleteAccount(accountId); }
            else { wands?.DeleteProfile(accountId, profileId); }
            journal.RevokeCustomerProfile(accountId, profileId);
            foreach (var session in journal.CustomerSessions(accountId).Where(session =>
                (profileId is null || session.ProfileId == profileId) && session.ConversationId is not null))
            { conversations.InvalidateMemoryContext(session.DeviceId, session.ConversationId); }
            return Task.CompletedTask;
        }, token);
    }
}

internal sealed class CharacterEnrichmentWorker(IServiceScopeFactory scopes, CharacterProfileStore store,
    ICharacterEnrichmentProvider provider, ILogger<CharacterEnrichmentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var reconciliationScope = scopes.CreateScope();
                var logs = reconciliationScope.ServiceProvider.GetService<ServerLogStore>();
                await CharacterEndpoints.ReconcileDeletionsAsync(reconciliationScope.ServiceProvider, store, stoppingToken);
                var job = await store.ClaimAsync(stoppingToken);
                if (job is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }
                logs?.Write("info", "character_job_started", operationId: Guid.Parse(job.Id).ToString(), attempt: job.Attempt);
                using var scope = scopes.CreateScope();
                var accounts = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
                var disabled = await accounts.Users.AsNoTracking().Where(user => user.Id == job.AccountId)
                    .Select(user => (bool?)user.Disabled).SingleOrDefaultAsync(stoppingToken);
                if (disabled is null)
                {
                    await store.FenceAccountDeletionAsync(job.AccountId, stoppingToken);
                    continue;
                }
                if (disabled.Value)
                {
                    await store.CompleteAsync(job, null, "account_unavailable", stoppingToken);
                    continue;
                }
                if (job.Attempt > 3)
                {
                    await store.CompleteAsync(job, null, "character_job_interrupted", stoppingToken);
                    continue;
                }
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(45));
                    if (!await store.CanDispatchAsync(job, deadline.Token)) { continue; }
                    var enrichment = await provider.EnrichAsync(job, deadline.Token);
                    // Recheck revocation after network work and before publishing a result.
                    disabled = await accounts.Users.AsNoTracking().Where(user => user.Id == job.AccountId)
                        .Select(user => (bool?)user.Disabled).SingleOrDefaultAsync(stoppingToken);
                    if (disabled is null)
                    {
                        await store.FenceAccountDeletionAsync(job.AccountId, stoppingToken);
                        continue;
                    }
                    if (disabled.Value)
                    {
                        await store.CompleteAsync(job, null, "account_unavailable", stoppingToken);
                        continue;
                    }
                    var completed = await store.CompleteAsync(job, enrichment, null, stoppingToken);
                    logs?.Write("info", completed ? "character_job_completed" : "character_job_discarded",
                        operationId: Guid.Parse(job.Id).ToString(), attempt: job.Attempt);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    logger.LogWarning("Character enrichment failed for job {JobId} ({ErrorType}).", job.Id, error.GetType().Name);
                    var code = CharacterProviderFailure.Code(error);
                    if (CharacterProviderFailure.IsTransient(error) && job.Attempt < 3)
                    {
                        var retried = await store.RetryAsync(job, stoppingToken);
                        logs?.Write("warning", retried ? "character_job_retry_scheduled" : "character_job_discarded",
                            status: CharacterProviderFailure.Status(error), operationId: Guid.Parse(job.Id).ToString(), code: code, attempt: job.Attempt);
                        continue;
                    }
                    var failed = await store.CompleteAsync(job, null, code, stoppingToken);
                    logs?.Write("warning", failed ? "character_job_failed" : "character_job_discarded",
                        status: CharacterProviderFailure.Status(error), operationId: Guid.Parse(job.Id).ToString(), code: code, attempt: job.Attempt);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogError("Character worker operation failed ({ErrorType}).", error.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
