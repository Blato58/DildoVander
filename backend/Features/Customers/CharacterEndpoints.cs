using System.Security.Claims;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.ClientModel;

namespace Kufr.Backend.Features.Customers;

internal static class CharacterEndpoints
{
    internal static IServiceCollection AddCustomerCharacters(this IServiceCollection services, IConfiguration configuration)
    {
        if (!AccountConfiguration.Read(configuration).Enabled) { return services; }
        services.AddSingleton(provider => new CharacterProfileStore(provider.GetRequiredService<AccountConfiguration>().DataRoot));
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
            Results.Ok(new { profile = await store.ReadAsync(AccountId(http), http.RequestAborted) }));
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
                await CharacterEndpoints.ReconcileDeletionsAsync(reconciliationScope.ServiceProvider, store, stoppingToken);
                var job = await store.ClaimAsync(stoppingToken);
                if (job is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }
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
                    await store.CompleteAsync(job, enrichment, null, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    logger.LogWarning("Character enrichment failed for job {JobId} ({ErrorType}).", job.Id, error.GetType().Name);
                    var transient = error is HttpRequestException or OperationCanceledException
                        || error is ClientResultException response && (response.Status is 408 or 429 || response.Status >= 500);
                    if (transient && job.Attempt < 3)
                    {
                        await store.RetryAsync(job, stoppingToken);
                        continue;
                    }
                    await store.CompleteAsync(job, null,
                        error is CharacterException character ? character.Code : "character_enrichment_failed", stoppingToken);
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
