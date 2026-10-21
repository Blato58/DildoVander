using System.Net;
using System.Net.Http.Json;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

internal static partial class AccountIdentityTests
{
    internal static async Task RunCustomerRegistrationAsync()
    {
        await using var fixture = await Fixture.StartAsync(customerFeatures: true);
        using var customer = fixture.Client();
        using var owner = fixture.Client();
        using var ownerKey = new VirtualPasskey();
        using var key = new VirtualPasskey();
        var path = "/app/v1/customer/enrollment";
        Check((await customer.PostAsJsonAsync(path + "/options", new { displayName = "Guest" })).StatusCode
            == HttpStatusCode.BadRequest, "public customer enrollment requires CSRF");
        await CsrfAsync(customer);
        Check((await customer.PostAsJsonAsync(path + "/options", new { displayName = "Guest" })).StatusCode
            == HttpStatusCode.ServiceUnavailable, "public signup cannot preempt owner bootstrap");
        await EnrollAsync(owner, ownerKey, Fixture.Bootstrap, "Owner");
        customer.DefaultRequestHeaders.Remove("Origin");
        customer.DefaultRequestHeaders.Add("Origin", "https://wrong.example");
        Check((await customer.PostAsJsonAsync(path + "/options", new { displayName = "Guest" })).StatusCode
            == HttpStatusCode.Forbidden, "customer registration checks exact origin");
        customer.DefaultRequestHeaders.Remove("Origin");
        customer.DefaultRequestHeaders.Add("Origin", "https://localhost");
        var options = await JsonAsync(await customer.PostAsJsonAsync(path + "/options",
            new { displayName = "Guest", role = "Owner", userId = "forged-owner" }));
        var credential = key.Create(options);
        Check((await customer.PostAsJsonAsync(path + "/complete", new { credential, role = "Owner" })).IsSuccessStatusCode,
            "customer completes verified passkey signup");
        var identity = await CsrfAsync(customer);
        var account = identity.GetProperty("account");
        var customerId = account.GetProperty("id").GetString()!;
        Check(account.GetProperty("roles").EnumerateArray().Select(role => role.GetString()).SequenceEqual(["Customer"]),
            "registration role is exclusively server-selected Customer");
        Check((await customer.GetAsync("/app/v1/admin/users")).StatusCode == HttpStatusCode.Forbidden,
            "customer has no account administration permission");
        await SeedAsync(fixture.App, async db =>
        {
            db.ResourceGrants.Add(new AccountResourceGrant
            {
                Id = Guid.NewGuid().ToString("N"), UserId = customerId,
                ResourceType = "device", ResourceId = "granted-device", Permission = "manage"
            });
            await db.SaveChangesAsync();
        });
        var details = await JsonAsync(await customer.GetAsync("/app/v1/account"));
        Check(details.GetProperty("devices").GetArrayLength() == 0,
            "Customer cannot inherit staff device capabilities even from erroneous resource grants");
        var initial = await JsonAsync(await customer.PutAsJsonAsync("/app/v1/customer/character",
            new CharacterWriteRequest(0, new("  Gandalf  ", "The Lord of the Rings", Description: "My own version"))));
        var profile = initial.GetProperty("profile");
        var profileId = profile.GetProperty("profileId").GetString()!;
        var revision = profile.GetProperty("revision").GetInt64();
        Check(profile.GetProperty("userInput").GetProperty("name").GetString() == "  Gandalf  ",
            "UserInput preserves exact customer text");
        await CsrfAsync(owner);
        var ownerProfile = await JsonAsync(await owner.GetAsync("/app/v1/customer/character"));
        Check(ownerProfile.GetProperty("profile").ValueKind == System.Text.Json.JsonValueKind.Null,
            "account-derived profile read cannot access another account");
        Check((await customer.PutAsJsonAsync("/app/v1/customer/character",
            new CharacterWriteRequest(0, new("Frodo", "Middle-earth")))).StatusCode == HttpStatusCode.Conflict,
            "stale profile edits are rejected");
        customer.DefaultRequestHeaders.Remove("X-DV-CSRF");
        Check((await customer.DeleteAsync($"/app/v1/customer/character?expectedRevision={revision}&expectedProfileId={profileId}")).StatusCode
            == HttpStatusCode.BadRequest, "profile deletion requires CSRF");
        await CsrfAsync(customer);
        Check((await customer.DeleteAsync($"/app/v1/customer/character?expectedRevision={revision}&expectedProfileId={profileId}")).IsSuccessStatusCode,
            "own profile deletion succeeds");
        Check((await customer.DeleteAsync("/app/v1/session")).IsSuccessStatusCode, "customer logout");
        await CsrfAsync(customer);
        Check((await customer.PostAsJsonAsync(path + "/complete", new { credential })).StatusCode == HttpStatusCode.BadRequest,
            "customer attestation ceremony cannot be replayed");
        await SignInAsync(customer, key);
        var signedBackIn = await CsrfAsync(customer);
        Check(signedBackIn.GetProperty("account").GetProperty("id").GetString() == customerId,
            "customer passkey uses existing durable sign-in flow");
        Console.WriteLine("Customer passkey registration, CSRF/origin, role and account isolation passed.");
    }

    private sealed class CustomerTestEnrichmentProvider : ICharacterEnrichmentProvider
    {
        public Task<CharacterEnrichment> EnrichAsync(CharacterJob job, CancellationToken token) => Task.FromResult(
            new CharacterEnrichment(job.UserInput.Name, job.UserInput.World, job.UserInput.Description ?? "Synthetic character fixture.",
                [], ["Synthetic conversation idea."], [], false, [], "fake", "fake-v1",
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), job.Revision, "synthetic"));
    }
}
