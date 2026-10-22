using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.Memory;
using Kufr.Backend.Features.PersonaLab;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Identity;

internal enum AccountDeletionRequestStatus { Accepted, LastOwner, NotFound }
internal sealed record AccountDeletionRequestResult(AccountDeletionRequestStatus Status, string? ReceiptId = null);
internal sealed class AccountDeletionUnavailableException : Exception;

// Durable identity-side saga. No provider or filesystem work occurs in the request transaction.
internal sealed class AccountDeletionService(AccountDatabase db, IServiceProvider services, TimeProvider clock,
    ILogger<AccountDeletionService>? logger = null)
{
    private (CharacterProfileStore Character, OperationJournal Journal, EveningMemoryStore Memory,
        KeepsakeStore Keepsakes, PersonaLabStore Lab, ConversationStore Conversations) Stores()
    {
        try
        {
            return (Required<CharacterProfileStore>(), Required<OperationJournal>(), Required<EveningMemoryStore>(),
                Required<KeepsakeStore>(), Required<PersonaLabStore>(), Required<ConversationStore>());
        }
        catch (Exception) { throw new AccountDeletionUnavailableException(); }
    }
    private T Required<T>() where T : notnull
        => services.GetService<T>() ?? throw new AccountDeletionUnavailableException();

    internal async Task<AccountDeletionRequestResult> RequestAsync(string accountId, CancellationToken token = default)
    {
        _ = Stores();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        // The first write serializes competing owner changes before the active-owner count.
        await db.Database.ExecuteSqlRawAsync("UPDATE AspNetUsers SET Disabled=Disabled WHERE Id={0}", [accountId], token);
        var prior = await db.DeletionRequests.SingleOrDefaultAsync(x => x.UserId == accountId, token);
        if (prior is not null) { return new(AccountDeletionRequestStatus.Accepted, prior.ReceiptId); }
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == accountId, token);
        if (user is null) { return new(AccountDeletionRequestStatus.NotFound); }
        var owners = from u in db.Users
                     join ur in db.UserRoles on u.Id equals ur.UserId
                     join role in db.Roles on ur.RoleId equals role.Id
                     where role.Name == AccountRoles.Owner && !u.Disabled
                     select u.Id;
        if (await owners.AnyAsync(x => x == accountId, token) && await owners.CountAsync(token) <= 1)
        { return new(AccountDeletionRequestStatus.LastOwner); }
        var request = new AccountDeletionRequest
        {
            UserId = accountId, ReceiptId = Guid.NewGuid().ToString("N"),
            RequestedAt = clock.GetUtcNow().ToUnixTimeSeconds()
        };
        db.DeletionRequests.Add(request);
        user.Disabled = true;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await RemoveExtensionsAsync(accountId, token);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(AccountDeletionRequestStatus.Accepted, request.ReceiptId);
    }

    internal async Task<string> ReadStatusAsync(string receiptId, CancellationToken token = default)
    {
        var receipt = await db.DeletionRequests.AsNoTracking().SingleOrDefaultAsync(x => x.ReceiptId == receiptId, token);
        return receipt is null ? "unknown" : receipt.CompletedAt is null ? "pending" : "completed";
    }

    internal async Task ProcessPendingAsync(CancellationToken token = default)
    {
        foreach (var request in await db.DeletionRequests.AsNoTracking().Where(x => x.CompletedAt == null).ToArrayAsync(token))
        {
            try
            {
                var tracked = db.DeletionRequests.Local.SingleOrDefault(x => x.UserId == request.UserId);
                if (tracked is not null) { db.Entry(tracked).State = EntityState.Detached; }
                db.Attach(request);
                request.Attempts++;
                await db.SaveChangesAsync(token);
                var stores = Stores();
                await stores.Character.FenceAccountDeletionAsync(request.UserId, token);
                stores.Journal.RevokeCustomerProfile(request.UserId);
                stores.Keepsakes.FenceAccountDeletion(request.UserId);
                stores.Lab.FenceAccountDeletion(request.UserId);
                // Complete inventories are taken only after every issuer has a permanent fence.
                foreach (var session in stores.Journal.CustomerSessions(request.UserId, deletionInventory: true))
                {
                    if (session.ConversationId is not { Length: > 0 } conversation) { continue; }
                    while (!stores.Memory.DeleteScopedMemory(session.DeviceId, "conversation", conversation, stores.Memory.MemoryEpoch))
                    { token.ThrowIfCancellationRequested(); }
                    stores.Memory.ScrubAccountConversation(session.DeviceId, conversation);
                    stores.Conversations.DeleteVisualContext(session.DeviceId, conversation);
                    stores.Conversations.InvalidateMemoryContext(session.DeviceId, conversation);
                }
                services.GetService<HybridMemoryService>()?.ClearCache();
                if (!stores.Keepsakes.DeleteAccountContent(request.UserId)) { continue; }
                if (services.GetService<PersonaLabRuntime>() is { } runtime) { runtime.DeleteAccountContent(request.UserId); }
                else { stores.Lab.DeleteAccountContent(request.UserId); }
                services.GetService<PersonaLabImages>()?.FenceAccountDeletion(request.UserId);
                await stores.Character.CompleteDeletionAsync(request.UserId, "*");
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                await RemoveExtensionsAsync(request.UserId, token);
                await db.Users.Where(x => x.Id == request.UserId).ExecuteDeleteAsync(token);
                request.CompletedAt = clock.GetUtcNow().ToUnixTimeSeconds();
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                // Retry from permanent fences after restart. Never persist exception content or PII.
                logger?.LogWarning("Account deletion cleanup remains pending for retry.");
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task RemoveExtensionsAsync(string id, CancellationToken token)
    {
        await db.Sessions.Where(x => x.UserId == id).ExecuteDeleteAsync(token);
        await db.ResourceGrants.Where(x => x.UserId == id).ExecuteDeleteAsync(token);
        await db.DeviceAssociations.Where(x => x.UserId == id).ExecuteDeleteAsync(token);
        await db.ArchiveTickets.Where(x => x.TargetUserId == id).ExecuteDeleteAsync(token);
        await db.ArchiveAssociations.Where(x => x.UserId == id).ExecuteDeleteAsync(token);
        await db.Ceremonies.Where(x => x.UserId == id).ExecuteDeleteAsync(token);
        await db.Invitations.Where(x => x.UserId == id).ExecuteDeleteAsync(token);
    }
}

internal sealed class AccountDeletionWorker(IServiceScopeFactory scopes, ILogger<AccountDeletionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<AccountDeletionService>().ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Account deletion retry is temporarily unavailable."); }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }
}
