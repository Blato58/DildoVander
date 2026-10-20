using System.Security.Claims;
using System.Security.Cryptography;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.PersonaLab;

internal sealed record LabHardwareRequest(string DeviceId, Guid BootId, int ExpectedRevision);
internal sealed record LabHardwareMetadata(string DeviceId, Guid BootId, int ExpectedRevision,
    DateTimeOffset ExpiresAt, string? ConversationId = null, string Status = "prepared")
{
    public string? OperationId { get; init; }
    public string? ProposalId { get; init; }
    public string? OutcomeId { get; init; }
    public string? PresentationEvidence { get; init; }
    public string? CloseCode { get; init; }
}

internal sealed class LabHardwareContext(LabHardwareLease lease, LabHardwareRequest target,
    string accountSessionId, string securityStamp, PersonaLabRuntime runtime, Func<CancellationToken, Task<bool>> authority)
{
    internal PersonaLabRuntime Runtime { get; } = runtime;
    internal DateTimeOffset TicketExpiresAt { get; init; } = DateTimeOffset.UtcNow.AddSeconds(90);
    internal LabSession Session { get; } = lease.Session;
    internal LabHardwareRequest Target { get; } = target;
    internal string AccountSessionId { get; } = accountSessionId;
    internal string SecurityStamp { get; } = securityStamp;
    internal CancellationToken Token => Lease.Token;
    internal DateTimeOffset ExpiresAt => Lease.ExpiresAt;
    internal Guid? CreateKey { get; set; }
    internal string? CreateHash { get; set; }
    internal string? ConversationId { get; set; }
    internal LabHardwareLease Lease { get; } = lease;
    internal async Task EnsureAuthorizedAsync(CancellationToken token)
    {
        try
        {
            Token.ThrowIfCancellationRequested();
            if (await authority(token)) { Token.ThrowIfCancellationRequested(); return; }
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested || token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            MarkClosing("authority_check_failed");
            Lease.Cancel();
            throw new OperationCanceledException(Token);
        }
        MarkClosing("authority_revoked");
        Lease.Cancel();
        throw new OperationCanceledException(Token);
    }
    private string? closeCode;
    internal string? CloseCode => Volatile.Read(ref closeCode);
    internal void MarkClosing(string code) => Interlocked.CompareExchange(ref closeCode, code, null);
    private readonly Lock observationGate = new();
    private readonly HashSet<string> observedProviders = new(StringComparer.Ordinal);
    internal void ProviderReady(string? providerSessionId)
    {
        lock (observationGate)
        {
            if (Token.IsCancellationRequested || !observedProviders.Add(providerSessionId ?? "unknown")) { return; }
            Runtime.ObserveHardware(Session, LabContract.Event("connection_started", "hardware_provider"));
            Runtime.ObserveHardware(Session, LabContract.Event("connection_established", "connected"));
            Runtime.ObserveHardware(Session, LabContract.Event("provider_session", "ready", providerSessionId: providerSessionId));
        }
    }
}

// This registry carries an explicit, short-lived capability into the existing
// conversation owner. It never opens a provider or owns a physical operation.
internal sealed class PersonaLabHardware(PersonaLabRuntime runtime, PersonaLabStore store,
    ConversationStore conversations, IServiceScopeFactory scopes, IConfiguration configuration, TimeProvider? clock = null) : IHostedService, IDisposable
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Lock gate = new();
    private readonly Dictionary<string, LabHardwareContext> tickets = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stopping = new();
    private readonly Dictionary<LabHardwareContext, Task> workers = [];

    internal async Task<object> MintAsync(HttpContext http, string id, LabHardwareRequest request)
    {
        if (request.DeviceId is not { Length: > 0 and <= 128 } || request.BootId == Guid.Empty || request.ExpectedRevision < 0)
        { throw new LabException(400, "invalid_lab_hardware_target"); }
        var owner = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new LabException(401, "account_required");
        var sid = http.User.FindFirstValue(AccountIdentity.SessionClaim) ?? throw new LabException(401, "account_required");
        var users = http.RequestServices.GetRequiredService<UserManager<AccountUser>>();
        var user = await users.FindByIdAsync(owner);
        if (user is null || user.Disabled || user.SecurityStamp != http.User.FindFirstValue(users.Options.ClaimsIdentity.SecurityStampClaimType))
        { throw new LabException(401, "account_required"); }
        // Check persistent authority before taking the shared runtime admission.
        if (!await AuthorizedAsync(owner, sid, user.SecurityStamp ?? "", request.DeviceId, http.RequestAborted))
        { throw new LabException(403, "device_operate_required"); }
        var lease = runtime.ReserveHardware(owner, id);
        var context = new LabHardwareContext(lease, request, sid, user.SecurityStamp ?? "", runtime,
            async token => await AuthorizedAsync(owner, sid, user.SecurityStamp ?? "", request.DeviceId, token)
                && store.Session(owner, id).Status == "open") { TicketExpiresAt = time.GetUtcNow().AddSeconds(90) };
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expires = context.TicketExpiresAt;
        try
        {
            lock (gate)
            {
                stopping.Token.ThrowIfCancellationRequested();
                tickets.Add(LabContract.Hash(token), context);
                store.Change(owner, id, value => value with
                { Hardware = new(request.DeviceId, request.BootId, request.ExpectedRevision, context.ExpiresAt) });
                // The worker cannot remove itself before registration: its
                // completion takes this same gate.
                workers.Add(context, Task.Run(() => WatchAsync(context, expires)));
            }
            runtime.ObserveHardware(context.Session, LabContract.Event("hardware_ticket", "explicit_device_binding"));
            return new { testContext = token, expiresAt = expires };
        }
        catch
        {
            lock (gate) { tickets.Remove(LabContract.Hash(token)); }
            lease.Dispose();
            throw;
        }
    }

    internal async Task<LabHardwareContext> BindAsync(string token, string device, Guid key, string hash,
        CreateConversationRequest request, CancellationToken cancellation)
    {
        if (!ValidToken(token)) { throw new LabException(401, "invalid_lab_hardware_ticket"); }
        LabHardwareContext context;
        lock (gate)
        {
            if (!tickets.TryGetValue(LabContract.Hash(token), out context!))
            { throw new LabException(401, "invalid_lab_hardware_ticket"); }
        }
        try { await context.EnsureAuthorizedAsync(cancellation); }
        catch (OperationCanceledException) { throw new LabException(403, "device_operate_required"); }
        lock (gate)
        {
            if (context.Token.IsCancellationRequested || context.ConversationId is null && context.TicketExpiresAt <= time.GetUtcNow() || device != context.Target.DeviceId || request.BootId != context.Target.BootId
                || request.CatalogVersion != PartyWandCatalog.Version || !request.Consent.Audio || request.Consent.Image)
            { throw new LabException(409, "lab_hardware_binding_mismatch"); }
            if (context.CreateKey is { } previous && (previous != key || context.CreateHash != hash))
            { throw new LabException(409, "lab_hardware_ticket_consumed"); }
            context.CreateKey = key;
            context.CreateHash = hash;
            return context;
        }
    }

    internal void Created(LabHardwareContext context, string conversation)
    {
        lock (gate)
        {
            if (context.Token.IsCancellationRequested) { throw new LabException(409, "lab_hardware_ended"); }
            context.ConversationId = conversation;
            store.Change(context.Session.OwnerId, context.Session.Id, value => value with
            { Hardware = new(context.Target.DeviceId, context.Target.BootId, context.Target.ExpectedRevision, context.ExpiresAt, conversation, "active") });
        }
    }

    internal void ObserveOutcome(string owner, string sessionId, string device, string operation,
        SelectionProposal proposal, string outcome, string evidence)
    {
        try
        {
            store.Change(owner, sessionId, session =>
            {
                if (session.Hardware is not { } target || target.DeviceId != device
                    || target.ConversationId != proposal.ConversationId) { return session; }
                var id = "hardware-outcome:" + outcome;
                if (session.Events.Any(value => value.Id == id)) { return session; }
                return session with
                {
                    Hardware = target with { OperationId = operation, ProposalId = proposal.ProposalId,
                        OutcomeId = outcome, PresentationEvidence = evidence },
                    Events = [.. session.Events, new LabEvent(id, "hardware_outcome", DateTimeOffset.UtcNow,
                        "completed_by_timing", Decision: "observed_device_report", Compartment: proposal.Compartment)]
                };
            });
        }
        catch (LabException error) when (error.Status == 404) { }
    }

    private async Task WatchAsync(LabHardwareContext context, DateTimeOffset ticketExpiry)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (!stopping.IsCancellationRequested && !context.Token.IsCancellationRequested)
            {
                bool unusedExpired;
                lock (gate) { unusedExpired = context.ConversationId is null && ticketExpiry <= time.GetUtcNow(); }
                if (unusedExpired) { context.MarkClosing("ticket_expired"); break; }
                if (!await AuthorizedAsync(context.Session.OwnerId, context.AccountSessionId,
                        context.SecurityStamp, context.Target.DeviceId, stopping.Token))
                { context.MarkClosing("authority_revoked"); break; }
                if (store.Session(context.Session.OwnerId, context.Session.Id).Status != "open")
                { context.MarkClosing("user_ended"); break; }
                await timer.WaitForNextTickAsync(stopping.Token);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested || context.Token.IsCancellationRequested) { }
        catch (LabException error) when (error.Status == 404) { context.MarkClosing("user_ended"); }
        catch (Exception) { context.MarkClosing("authority_check_failed"); }
        finally
        {
            if (stopping.IsCancellationRequested) { context.MarkClosing("backend_stopping"); }
            else if (context.ExpiresAt <= DateTimeOffset.UtcNow) { context.MarkClosing("lease_expired"); }
            else { context.MarkClosing("user_ended"); }
            context.Lease.Cancel();
            // No synchronous cancellation callback takes the conversation/runtime
            // locks. Teardown precedes release of the shared Lab capacity.
            try
            {
                conversations.EndLabConversation(context);
                store.Change(context.Session.OwnerId, context.Session.Id, value => value.Hardware is { } metadata
                    ? value with
                    {
                        Hardware = metadata with { Status = context.CloseCode is "authority_revoked" or "authority_check_failed" ? "failed" : "ended",
                            CloseCode = context.CloseCode },
                        Events = context.CloseCode is "authority_revoked" or "authority_check_failed"
                            ? [.. value.Events, LabContract.Event("hardware_closed", context.CloseCode),
                                LabContract.Event("provider_failure", context.CloseCode)]
                            : [.. value.Events, LabContract.Event("hardware_closed", context.CloseCode)]
                    } : value);
            }
            catch (LabException error) when (error.Status == 404) { }
            finally
            {
                lock (gate)
                {
                    foreach (var key in tickets.Where(pair => ReferenceEquals(pair.Value, context)).Select(pair => pair.Key).ToArray())
                    { tickets.Remove(key); }
                }
                context.Lease.Dispose();
                lock (gate) { workers.Remove(context); }
            }
        }
    }

    private async Task<bool> AuthorizedAsync(string owner, string sid, string stamp, string device, CancellationToken token)
    {
        if (!configuration.GetSection("Devices").GetChildren().Any(value => value["DeviceId"] == device)) { return false; }
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        if (!await db.Sessions.AsNoTracking().AnyAsync(value => value.Id == sid && value.UserId == owner && value.ExpiresAt > now, token)
            || !await db.Users.AsNoTracking().AnyAsync(value => value.Id == owner && !value.Disabled && value.SecurityStamp == stamp, token))
        { return false; }
        var roles = await (from membership in db.UserRoles join role in db.Roles on membership.RoleId equals role.Id
            where membership.UserId == owner select role.Name).ToArrayAsync(token);
        return roles.Contains(AccountRoles.Owner) || roles.Contains(AccountRoles.Administrator)
            || roles.Contains(AccountRoles.Operator) && await db.ResourceGrants.AsNoTracking().AnyAsync(value => value.UserId == owner
                && value.ResourceType == "device" && value.ResourceId == device && (value.Permission == "operate" || value.Permission == "manage"), token);
    }

    private static bool ValidToken(string token) => token.Length == 43
        && token.All(value => char.IsAsciiLetterOrDigit(value) || value is '-' or '_');

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancelAll();
        Task[] snapshot;
        lock (gate) { snapshot = workers.Values.ToArray(); }
        var drain = Task.WhenAll(snapshot);
        try { await drain.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
        catch (Exception) when (!drain.IsCompletedSuccessfully) { }
        // Keep the CTS valid until every bounded worker has unwound, including
        // when the host's shutdown deadline ends before database cancellation.
        if (drain.IsCompleted) { _ = drain.Exception; }
    }

    private void CancelAll()
    {
        stopping.Cancel();
        lock (gate) { foreach (var context in tickets.Values) { context.Lease.Cancel(); } }
    }

    public void Dispose() => CancelAll();
}

internal static class PersonaLabHardwareEndpoints
{
    internal static RouteGroupBuilder MapPersonaLabHardware(this RouteGroupBuilder group)
    {
        group.MapPost("/sessions/{id}/hardware-ticket", async (HttpContext http, string id,
            LabHardwareRequest request, [Microsoft.AspNetCore.Mvc.FromServices] PersonaLabHardware hardware) => Results.Json(await hardware.MintAsync(http, id, request)));
        return group;
    }
}
