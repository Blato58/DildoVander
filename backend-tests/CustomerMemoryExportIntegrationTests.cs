using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kufr.Backend.Features.CustomerData;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Memory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

internal static partial class AccountIdentityTests
{
    internal static async Task RunCustomerMemoryExportIntegrationAsync()
    {
        await using var fixture = await Fixture.StartAsync(customerMemoryFeatures: true,
            accountDataExportFeatures: true, deletionFeatures: true);
        using var owner = fixture.Client();
        using var ownerKey = new VirtualPasskey();
        await EnrollAsync(owner, ownerKey, Fixture.Bootstrap, "Memory export integration owner");
        await CsrfAsync(owner);

        using var customer = fixture.Client();
        using var customerKey = new VirtualPasskey();
        var profile = await CustomerAsync(customer, customerKey);
        var account = await CsrfAsync(customer);
        var accountId = account.GetProperty("account").GetProperty("id").GetString()!;
        using var memory = await CustomerMemoryHarness.CreateAsync(fixture.App, accountId, profile);

        var services = fixture.App.Services;
        var productionMemory = services.GetRequiredService<CustomerMemoryData>();
        var productionHybrid = services.GetRequiredService<HybridMemoryService>();
        Check(ReferenceEquals(productionMemory, services.GetRequiredService<ICustomerDataMemoryAdmission>())
            && ReferenceEquals(productionMemory, services.GetRequiredService<ICustomerMemoryGuardedDataSource>())
            && ReferenceEquals(services.GetRequiredService<AccountDataExportAuthority>(),
                services.GetRequiredService<ICustomerDataAuthority>()),
            "account export and customer-memory HTTP use the same production guarded-memory and authority singletons");
        Check(productionHybrid is not null && productionMemory.DatabasePath == memory.Guarded.DatabasePath
            && services.GetRequiredService<HybridMemoryOptions>().Namespace == memory.Options.Namespace,
            "integration uses the real HybridMemoryService registration and the same SQLite memory store");

        SeedAllowedRecord(memory, "Original customer reported fact", "answer-501");
        var outcomeId = Guid.NewGuid().ToString("D");
        var outcome = memory.Store.WriteMemory(memory.AccessFor(memory.Guest), new(outcomeId,
            MemoryScope.Personal, MemoryKind.ConfirmedSelection, "Confirmed application outcome", "answer-502"))
            ?? throw new InvalidOperationException("confirmed-selection export fixture was not stored");
        SeedSyntheticRecord(memory, "Another guest's private export fact", "foreign-guest",
            "Personal", memory.Options.Namespace, "UserReportedFact");

        var exports = services.GetRequiredService<AccountDataExportService>();
        var exportStore = services.GetRequiredService<AccountDataExportStore>();
        const string exportPath = "/app/v1/customer/data/exports";

        var initialId = await RequestAndPrepareExportAsync(customer, exports, exportPath);
        using (var initialDownload = await customer.GetAsync($"{exportPath}/{initialId}/download"))
        {
            Check(initialDownload.StatusCode == HttpStatusCode.OK
                && initialDownload.Headers.CacheControl?.NoStore == true,
                "real HTTPS export downloads the completed memory-backed account snapshot without caching");
            using var document = JsonDocument.Parse(await initialDownload.Content.ReadAsByteArrayAsync());
            var facts = document.RootElement.GetProperty("memoryFacts").EnumerateArray().ToArray();
            Check(facts.Length == 2
                && facts.Select(fact => fact.GetProperty("id").GetString()).ToHashSet(StringComparer.Ordinal)
                    .SetEquals([memory.MemoryId, outcomeId])
                && facts.Any(fact => fact.GetProperty("text").GetString() == "Original customer reported fact"
                    && fact.GetProperty("kind").GetString() == nameof(MemoryKind.UserReportedFact))
                && facts.Any(fact => fact.GetProperty("text").GetString() == "Confirmed application outcome"
                    && fact.GetProperty("kind").GetString() == nameof(MemoryKind.ConfirmedSelection))
                && facts.All(fact => fact.GetProperty("profileId").GetString() == memory.ProfileId
                    && fact.GetProperty("customerSessionId").GetString() == memory.CustomerSession.Id
                    && !fact.TryGetProperty("guestId", out _)
                    && !fact.TryGetProperty("deviceId", out _)
                    && !fact.TryGetProperty("conversationId", out _)),
                "export preserves only exact original-owner reported and confirmed memory provenance");
            var wire = document.RootElement.GetRawText();
            Check(!wire.Contains("Another guest's private export fact", StringComparison.Ordinal)
                && !wire.Contains(memory.Guest.GuestId, StringComparison.Ordinal)
                && !wire.Contains("foreign-guest", StringComparison.Ordinal)
                && !wire.Contains(memory.Access.AuthorityRevision, StringComparison.Ordinal)
                && !wire.Contains("securityStamp", StringComparison.Ordinal)
                && !wire.Contains("authorityRevision", StringComparison.Ordinal),
                "export excludes foreign memory, guest identity, and internal authority proofs while retaining allowed session metadata");
        }

        var correctionPath = "/app/v1/customer/memory/" + memory.MemoryId;
        await CsrfAsync(customer);
        using (var correction = await customer.PutAsJsonAsync(correctionPath,
                   new CustomerMemoryCorrectionRequest(1, "Corrected customer reported fact")))
        {
            Check(correction.StatusCode == HttpStatusCode.NoContent,
                "the same real-memory HTTPS pipeline accepts a correction before export revalidation");
        }
        await CheckExportInvalidatedAsync(customer, exportPath, initialId,
            "correction invalidates the ready export proof before another download");

        var correctedExportId = await RequestAndPrepareExportAsync(customer, exports, exportPath);
        using (var correctedDownload = await customer.GetAsync($"{exportPath}/{correctedExportId}/download"))
        {
            Check(correctedDownload.StatusCode == HttpStatusCode.OK,
                "a new export can be prepared after the customer correction");
            using var document = JsonDocument.Parse(await correctedDownload.Content.ReadAsByteArrayAsync());
            var facts = document.RootElement.GetProperty("memoryFacts").EnumerateArray().ToArray();
            Check(facts.Length == 2 && facts.Any(fact => fact.GetProperty("text").GetString()
                    == "Corrected customer reported fact"),
                "new export contains the current corrected memory revision");
        }

        await CsrfAsync(customer);
        using (var forget = await customer.PostAsJsonAsync($"/app/v1/customer/memory/{outcomeId}/forget",
                   new CustomerMemoryForgetRequest(outcome.Revision, Confirmed: true)))
        {
            Check(forget.StatusCode == HttpStatusCode.NoContent,
                "the original owner can explicitly forget the confirmed selection through HTTPS");
        }
        await CheckExportInvalidatedAsync(customer, exportPath, correctedExportId,
            "forgetting a confirmed selection invalidates the ready export proof before download");

        var postForgetExportId = await RequestAndPrepareExportAsync(customer, exports, exportPath);
        using (var postForgetDownload = await customer.GetAsync($"{exportPath}/{postForgetExportId}/download"))
        {
            Check(postForgetDownload.StatusCode == HttpStatusCode.OK,
                "a new export can be prepared after forgetting the selection");
            using var document = JsonDocument.Parse(await postForgetDownload.Content.ReadAsByteArrayAsync());
            var facts = document.RootElement.GetProperty("memoryFacts").EnumerateArray().ToArray();
            Check(facts.Length == 1 && facts[0].GetProperty("text").GetString() == "Corrected customer reported fact",
                "later export excludes the erased confirmed selection and retains the unrelated current fact");
        }

        ExpireExportNow(exportStore, postForgetExportId);
        using (var expiredStatus = await customer.GetAsync($"{exportPath}/{postForgetExportId}"))
        {
            var status = await JsonAsync(expiredStatus);
            Check(status.GetProperty("state").GetString() == "failed"
                && status.GetProperty("reason").GetString() == "download_expired",
                "expired account export clears its ready proof on the real memory-backed service");
        }

        var accountDetails = await JsonAsync(await customer.GetAsync("/app/v1/account"));
        var currentSession = accountDetails.GetProperty("sessions").EnumerateArray()
            .Single(session => session.GetProperty("current").GetBoolean()).GetProperty("id").GetString()!;
        await CsrfAsync(customer);
        using (var revoke = await customer.DeleteAsync($"/app/v1/sessions/{currentSession}"))
        {
            Check(revoke.StatusCode == HttpStatusCode.NoContent,
                "account can revoke the session bound to its real memory export");
        }
        using (var revokedSession = await customer.GetAsync($"{exportPath}/{postForgetExportId}"))
        {
            Check(revokedSession.StatusCode == HttpStatusCode.Unauthorized,
                "revoked account session cannot inspect the memory-backed export job");
        }

        using var stampedCustomer = fixture.Client();
        using var stampedKey = new VirtualPasskey();
        await CustomerAsync(stampedCustomer, stampedKey);
        var stampedIdentity = await CsrfAsync(stampedCustomer);
        var stampedAccountId = stampedIdentity.GetProperty("account").GetProperty("id").GetString()!;
        var stampedJob = await RequestAndPrepareExportAsync(stampedCustomer, exports, exportPath);
        await SeedAsync(fixture.App, db => db.Users.Where(user => user.Id == stampedAccountId)
            .ExecuteUpdateAsync(update => update.SetProperty(user => user.SecurityStamp,
                "memory-export-revoked-" + Guid.NewGuid().ToString("N"))));
        using (var revokedStamp = await stampedCustomer.GetAsync($"{exportPath}/{stampedJob}"))
        {
            Check(revokedStamp.StatusCode == HttpStatusCode.Unauthorized,
                "security-stamp revocation denies a ready memory-backed export");
        }

        using var deletingCustomer = fixture.Client();
        using var deletingKey = new VirtualPasskey();
        await CustomerAsync(deletingCustomer, deletingKey);
        var deletingIdentity = await CsrfAsync(deletingCustomer);
        var deletingAccountId = deletingIdentity.GetProperty("account").GetProperty("id").GetString()!;
        await SignInAsync(deletingCustomer, deletingKey);
        await CsrfAsync(deletingCustomer);
        var deletionJob = await RequestAndPrepareExportAsync(deletingCustomer, exports, exportPath);
        await CsrfAsync(deletingCustomer);
        using (var deletion = await deletingCustomer.DeleteAsync("/app/v1/account"))
        {
            Check(deletion.StatusCode == HttpStatusCode.Accepted,
                "recently authenticated account deletion is accepted with export metadata registered");
        }
        using (var revokedDeletion = await deletingCustomer.GetAsync($"{exportPath}/{deletionJob}"))
        {
            Check(revokedDeletion.StatusCode == HttpStatusCode.Unauthorized,
                "accepted account deletion immediately revokes memory-backed export access");
        }
        using (var scope = services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AccountDeletionService>().ProcessPendingAsync();
        }
        Check(ExportJobCount(exportStore.DatabasePath, deletingAccountId) == 0,
            "the ordinary account-deletion service removes the account's export metadata");

        await CheckExportMetadataCleanupWithoutMappedExportRoutesAsync();

        Console.WriteLine("PASS customer memory plus account export: real guarded memory projection, correction/forget revision fencing, expiry, session/stamp revocation, and account-deletion cleanup");
    }

    private static async Task CheckExportMetadataCleanupWithoutMappedExportRoutesAsync()
    {
        await using var fixture = await Fixture.StartAsync(customerMemoryFeatures: true,
            deletionFeatures: true, accountDataExportFeatures: false);
        using var owner = fixture.Client();
        using var ownerKey = new VirtualPasskey();
        await EnrollAsync(owner, ownerKey, Fixture.Bootstrap, "Unmapped export cleanup owner");
        await CsrfAsync(owner);

        using var customer = fixture.Client();
        using var customerKey = new VirtualPasskey();
        await CustomerAsync(customer, customerKey);
        var identity = await CsrfAsync(customer);
        var accountId = identity.GetProperty("account").GetProperty("id").GetString()!;
        var services = fixture.App.Services;
        var exportStore = services.GetRequiredService<AccountDataExportStore>();
        _ = services.GetRequiredService<AccountDeletionService>();

        string sessionId;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            sessionId = await db.Sessions.AsNoTracking().Where(session => session.UserId == accountId)
                .Select(session => session.Id).SingleAsync();
        }

        // Seed only the metadata row needed to exercise the cleanup dependency;
        // the route is intentionally absent in this fixture configuration.
        var proof = new CustomerDataAccess(accountId, sessionId, "cleanup-fixture-proof");
        var job = exportStore.Create(proof, Guid.NewGuid());
        Check(ReferenceEquals(services.GetRequiredService<CustomerMemoryData>(),
                services.GetRequiredService<ICustomerDataMemoryAdmission>())
            && ReferenceEquals(services.GetRequiredService<CustomerMemoryData>(),
                services.GetRequiredService<ICustomerMemoryGuardedDataSource>()),
            "customer-memory configuration registers the real guarded singleton while export HTTP mapping is disabled");
        using (var unmapped = await customer.GetAsync($"/app/v1/customer/data/exports/{job.Id}"))
        {
            Check(unmapped.StatusCode == HttpStatusCode.NotFound,
                "disabling account-export route mapping leaves the export HTTP path unavailable");
        }

        await SignInAsync(customer, customerKey);
        await CsrfAsync(customer);
        using (var deletion = await customer.DeleteAsync("/app/v1/account"))
        {
            Check(deletion.StatusCode == HttpStatusCode.Accepted,
                "account deletion remains available when export routes are not mapped");
        }
        using (var scope = services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AccountDeletionService>().ProcessPendingAsync();
        }
        Check(ExportJobCount(exportStore.DatabasePath, accountId) == 0,
            "account deletion removes export metadata even when export HTTP routes are not mapped");

        var fenced = false;
        try { _ = exportStore.Create(proof, Guid.NewGuid()); }
        catch (AccountDataExportException error) when (error.Code == "account_export_deleted") { fenced = true; }
        Check(fenced, "account deletion preserves the export metadata fence without mapped export routes");
    }

    private static async Task<string> RequestAndPrepareExportAsync(HttpClient client,
        AccountDataExportService exports, string path)
    {
        using var accepted = await PostExportAsync(client, new { schemaVersion = 1 }, Guid.NewGuid().ToString("D"));
        Check(accepted.StatusCode == HttpStatusCode.Accepted, "owner export request accepted by HTTPS endpoint");
        var id = (await JsonAsync(accepted)).GetProperty("id").GetString()!;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await exports.PrepareNextAsync();
            using var statusResponse = await client.GetAsync($"{path}/{id}");
            var status = await JsonAsync(statusResponse);
            var state = status.GetProperty("state").GetString();
            if (state == "ready") { return id; }
            if (state == "failed")
            {
                throw new InvalidOperationException("Customer memory export preparation failed: "
                    + status.GetProperty("reason").GetString());
            }
            await Task.Delay(25);
        }
        throw new InvalidOperationException("Customer memory export did not become ready within the fixture deadline.");
    }

    private static async Task CheckExportInvalidatedAsync(HttpClient client, string path, string id, string assertion)
    {
        using var statusResponse = await client.GetAsync($"{path}/{id}");
        var status = await JsonAsync(statusResponse);
        Check(status.GetProperty("state").GetString() == "failed"
            && status.GetProperty("reason").GetString() == "source_changed", assertion);
        using var staleDownload = await client.GetAsync($"{path}/{id}/download");
        Check(staleDownload.StatusCode == HttpStatusCode.Conflict,
            "stale memory-backed export refuses a download after its source changed");
    }

    private static void ExpireExportNow(AccountDataExportStore store, string id)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = store.DatabasePath,
            DefaultTimeout = 2
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE account_data_export_jobs SET download_expires_at=0 WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() != 1) { throw new InvalidOperationException("expiration fixture did not find its export job"); }
    }

    private static long ExportJobCount(string databasePath, string accountId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            DefaultTimeout = 2
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM account_data_export_jobs WHERE account_id=$account";
        command.Parameters.AddWithValue("$account", accountId);
        return (long)command.ExecuteScalar()!;
    }
}
