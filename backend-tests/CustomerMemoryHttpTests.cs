using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Kufr.Backend.Features.CustomerData;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

internal static partial class AccountIdentityTests
{
    internal static async Task RunCustomerMemoryHttpAsync()
    {
        var escaped = new string('\u0001', 2000);
        var largeItems = Enumerable.Range(0, CustomerDataLimits.MaxMemoryItems)
            .Select(index => new CustomerMemoryItem(index.ToString(), 1, escaped,
                "UserReportedFact", "", DateTimeOffset.UtcNow, "profile", "session")).ToArray();
        try
        {
            CustomerMemoryEndpoints.SerializeList(new("inventory", "revision", largeItems, true));
            throw new InvalidOperationException("Escaped memory list exceeded its wire limit without rejection.");
        }
        catch (CustomerDataAccessException error) when (error.Status == 413 && error.Code == "export_size_limit")
        {
            // Persisted text limits do not by themselves bound escaped JSON.
        }
        await using var fixture = await Fixture.StartAsync(customerMemoryFeatures: true);
        using var owner = fixture.Client();
        using var ownerKey = new VirtualPasskey();
        await EnrollAsync(owner, ownerKey, Fixture.Bootstrap, "Memory HTTP fixture owner");
        await CsrfAsync(owner);
        using var customer = fixture.Client();
        using var key = new VirtualPasskey();
        var profile = await CustomerAsync(customer, key);
        var account = await CsrfAsync(customer);
        var accountId = account.GetProperty("account").GetProperty("id").GetString()!;
        using var data = await CustomerMemoryHarness.CreateAsync(fixture.App, accountId, profile);
        SeedAllowedRecord(data, "Only this customer's reported fact", "unsafe internal source");
        SeedSyntheticRecord(data, "Confirmed application outcome", data.Guest.GuestId,
            "Personal", data.Options.Namespace, "ConfirmedSelection");
        SeedSyntheticRecord(data, "Another guest's private fact", "another-guest",
            "Personal", data.Options.Namespace, "UserReportedFact");

        var services = fixture.App.Services;
        Check(ReferenceEquals(services.GetRequiredService<CustomerMemoryData>(),
                services.GetRequiredService<ICustomerDataMemoryAdmission>())
            && ReferenceEquals(services.GetRequiredService<CustomerMemoryData>(),
                services.GetRequiredService<ICustomerMemoryGuardedDataSource>()),
            "production memory registrations use one real admission and guarded-data singleton");
        var response = await customer.GetAsync("/app/v1/customer/memory");
        var list = await JsonAsync(response);
        Check(response.IsSuccessStatusCode && list.GetProperty("complete").GetBoolean()
            && list.GetProperty("revision").GetString()!.Length == 64,
            "real HTTPS memory list releases a complete bounded revision");
        Check(list.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(
                new[] { "complete", "items", "revision" }),
            "list wire contract excludes authority and inventory proofs");
        var items = list.GetProperty("items").EnumerateArray().ToArray();
        Check(items.Length == 2 && items.All(item => item.GetProperty("canForget").GetBoolean()),
            "only original-owner personal facts are listed with explicit forget capability");
        var fact = items.Single(item => item.GetProperty("kind").GetString() == "UserReportedFact");
        var outcome = items.Single(item => item.GetProperty("kind").GetString() == "ConfirmedSelection");
        Check(fact.GetProperty("canCorrect").GetBoolean()
            && !outcome.GetProperty("canCorrect").GetBoolean()
            && fact.GetProperty("sourceReference").GetString() == "",
            "reported facts can be corrected; confirmed outcome provenance cannot be rewritten");
        Check(response.Headers.CacheControl?.NoStore == true
            && response.Headers.GetValues("X-Content-Type-Options").Single() == "nosniff",
            "private memory responses retain the account no-store and nosniff headers");

        var factId = fact.GetProperty("id").GetString()!;
        var outcomeId = outcome.GetProperty("id").GetString()!;
        var correctionPath = "/app/v1/customer/memory/" + factId;
        customer.DefaultRequestHeaders.Remove("X-DV-CSRF");
        Check((await customer.PutAsJsonAsync(correctionPath,
                new CustomerMemoryCorrectionRequest(1, "Corrected own fact"))).StatusCode == HttpStatusCode.BadRequest,
            "memory correction requires the real account anti-forgery token");
        Check(data.Store.CurrentMemory(factId)!.Revision == 1,
            "failed anti-forgery admission does not mutate the fact");
        await CsrfAsync(customer);
        Check((await customer.PutAsJsonAsync(correctionPath,
                new CustomerMemoryCorrectionRequest(1, "Corrected own fact"))).StatusCode == HttpStatusCode.NoContent,
            "customer corrects an owned reported fact through real HTTPS and CSRF");
        Check((await customer.PutAsJsonAsync(correctionPath,
                new CustomerMemoryCorrectionRequest(1, "Stale correction"))).StatusCode == HttpStatusCode.Conflict,
            "HTTP stale-revision correction is rejected");
        Check((await customer.PutAsJsonAsync("/app/v1/customer/memory/" + outcomeId,
                new CustomerMemoryCorrectionRequest(1, "Invented outcome"))).StatusCode == HttpStatusCode.NotFound,
            "HTTP correction cannot overwrite a confirmed outcome's provenance");

        using var stranger = fixture.Client();
        using var strangerKey = new VirtualPasskey();
        await CustomerAsync(stranger, strangerKey);
        var strangerList = await JsonAsync(await stranger.GetAsync("/app/v1/customer/memory"));
        Check(strangerList.GetProperty("items").GetArrayLength() == 0,
            "same display name and character do not grant another account's memories");
        Check((await stranger.PutAsJsonAsync(correctionPath,
                new CustomerMemoryCorrectionRequest(2, "Foreign correction"))).StatusCode == HttpStatusCode.NotFound
            && (await stranger.PostAsJsonAsync(correctionPath + "/forget",
                new CustomerMemoryForgetRequest(2, true))).StatusCode == HttpStatusCode.NotFound,
            "another account cannot correct or forget a supplied owned record ID");
        Check((await customer.PostAsJsonAsync(correctionPath + "/forget",
                new CustomerMemoryForgetRequest(2, false))).StatusCode == HttpStatusCode.BadRequest,
            "HTTP forget requires an explicit confirmation");
        Check((await customer.PostAsJsonAsync(correctionPath + "/forget",
                new CustomerMemoryForgetRequest(2, true))).StatusCode == HttpStatusCode.NoContent
            && data.Store.CurrentMemory(factId) is { IsActive: false, Text: "" },
            "confirmed HTTP forget erases the original owner's fact");

        using var anonymous = fixture.Client();
        Check((await anonymous.GetAsync("/app/v1/customer/memory")).StatusCode == HttpStatusCode.Unauthorized,
            "anonymous memory list is denied by real account authorization");
        customer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-service-token");
        Check((await customer.GetAsync("/app/v1/customer/memory")).StatusCode == HttpStatusCode.Unauthorized,
            "service Authorization header is rejected even alongside a valid account cookie");
        customer.DefaultRequestHeaders.Authorization = null;

        await data.Profiles.DeleteAsync(accountId, data.ProfileRevision, data.ProfileId);
        var deletedProfileList = await JsonAsync(await customer.GetAsync("/app/v1/customer/memory"));
        Check(deletedProfileList.GetProperty("items").GetArrayLength() == 0,
            "profile tombstone immediately removes memory content from the HTTP list");
        Check((await customer.PostAsJsonAsync("/app/v1/customer/memory/" + outcomeId + "/forget",
                new CustomerMemoryForgetRequest(1, true))).StatusCode == HttpStatusCode.NoContent,
            "retained exact origin permits blind confirmed forget after profile deletion");
        await SeedAsync(fixture.App, database => database.Users.Where(user => user.Id == accountId)
            .ExecuteUpdateAsync(update => update.SetProperty(user => user.SecurityStamp, Guid.NewGuid().ToString("N"))));
        Check((await customer.GetAsync("/app/v1/customer/memory")).StatusCode == HttpStatusCode.Unauthorized,
            "security-stamp revocation denies memory reads through the actual cookie middleware");
        Console.WriteLine("PASS customer memory HTTP: real cookie scope, singleton admission, bounded wire contract, provenance capabilities, account isolation, tombstone and stamp revocation");
    }
}
