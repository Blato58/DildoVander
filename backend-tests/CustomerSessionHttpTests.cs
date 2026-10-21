using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Microsoft.Extensions.DependencyInjection;

internal static partial class AccountIdentityTests
{
    internal static async Task RunCustomerSessionsAsync()
    {
        await using var fixture = await Fixture.StartAsync(customerFeatures: true);
        using var owner = fixture.Client(); using var ownerKey = new VirtualPasskey();
        using var first = fixture.Client(); using var firstKey = new VirtualPasskey();
        using var second = fixture.Client(); using var secondKey = new VirtualPasskey();
        using var device = fixture.Client();
        await EnrollAsync(owner, ownerKey, Fixture.Bootstrap, "Owner");
        await CsrfAsync(owner);
        var firstProfile = await CustomerAsync(first, firstKey);
        var secondProfile = await CustomerAsync(second, secondKey);
        Check((await first.PutAsJsonAsync("/app/v1/customer/admin/devices/granted-device", new { enabled = true })).StatusCode
            == HttpStatusCode.Forbidden, "customer cannot enable the BLE mode");
        Check((await owner.PutAsJsonAsync("/app/v1/customer/admin/devices/granted-device", new { enabled = true })).IsSuccessStatusCode,
            "device manager explicitly enables customer admission");
        var boot = Guid.NewGuid().ToString("D");
        var challenge = new CustomerDeviceChallenge(1, boot, Guid.NewGuid().ToString("D"), 0, PartyWandCatalog.Version, "1");
        Check((await owner.PostAsJsonAsync("/v1/customer-sessions/device-challenge", challenge)).StatusCode
            == HttpStatusCode.Unauthorized, "account device grants never mint physical BLE challenges");
        var token = fixture.App.Services.GetRequiredService<DeviceAccessStore>().IssueToken("granted-device")!;
        device.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        Check((await device.PostAsJsonAsync("/v1/customer-sessions/device-challenge", challenge)).IsSuccessStatusCode,
            "only authenticated device registers a fresh boot challenge");
        var firstOp = Guid.NewGuid().ToString("D");
        var secondOp = Guid.NewGuid().ToString("D");
        var issuedFirst = await TicketAsync(first, firstProfile, firstOp);
        var issuedSecond = await TicketAsync(second, secondProfile, secondOp);
        var redeemFirst = new CustomerRedemption(1, issuedFirst.GetProperty("ticket").GetString()!, firstOp, boot, challenge.Challenge);
        var redeemSecond = new CustomerRedemption(1, issuedSecond.GetProperty("ticket").GetString()!, secondOp, boot, challenge.Challenge);
        var replies = await Task.WhenAll(device.PostAsJsonAsync("/v1/customer-sessions/redeem", redeemFirst),
            device.PostAsJsonAsync("/v1/customer-sessions/redeem", redeemSecond));
        Check(replies.Count(response => response.StatusCode == HttpStatusCode.Created) == 1,
            "real HTTPS simultaneous redemption starts exactly one account-owned conversation");
        var index = Array.FindIndex(replies, response => response.StatusCode == HttpStatusCode.Created);
        var winner = index == 0 ? first : second;
        var stranger = index == 0 ? second : first;
        var winningRedemption = index == 0 ? redeemFirst : redeemSecond;
        var accepted = await JsonAsync(replies[index]);
        var id = accepted.GetProperty("customerSessionId").GetString()!;
        var conversation = accepted.GetProperty("conversationId").GetString()!;
        Check((await stranger.GetAsync($"/app/v1/customer/sessions/{id}")).StatusCode == HttpStatusCode.NotFound,
            "other customer cannot inspect a known session UUID");
        Check((await stranger.PostAsJsonAsync($"/app/v1/customer/sessions/{id}/end", new { })).StatusCode == HttpStatusCode.NotFound,
            "other customer cannot end a known session UUID");
        var retry = await JsonAsync(await device.PostAsJsonAsync("/v1/customer-sessions/redeem", winningRedemption));
        Check(retry.GetProperty("customerSessionId").GetString() == id && retry.GetProperty("conversationId").GetString() == conversation,
            "lost-response retry returns the exact logical conversation");
        var binding = fixture.App.Services.GetRequiredService<EveningMemoryStore>().GuestPermission("granted-device", conversation)!;
        Check(binding.IdentityVerified && !binding.LongTermAllowed && !binding.CloudExportAllowed,
            "current profile use creates verified identity without implicit long-term/cloud consent");
        Check((await winner.PostAsJsonAsync($"/app/v1/customer/sessions/{id}/end", new { })).StatusCode == HttpStatusCode.Accepted,
            "owner requests only own session end");
        var polled = await JsonAsync(await device.GetAsync("/v1/customer-sessions/device"));
        Check(polled.GetProperty("endRequested").GetBoolean(), "autonomous device receives authorized end independently from phone BLE");
        var running = await device.PostAsJsonAsync($"/v1/customer-sessions/{id}/device-state", new { bootId = boot, state = "running", conversationId = conversation });
        Check(running.IsSuccessStatusCode, "device reports real provider start separately from context readiness");
        Check((await device.PostAsJsonAsync($"/v1/customer-sessions/{id}/device-state", new { bootId = boot, state = "completed", conversationId = conversation })).IsSuccessStatusCode,
            "physical runtime termination settles the owned session");
        Check((await JsonAsync(await device.GetAsync("/v1/customer-sessions/device"))).GetProperty("activeSessionId").ValueKind == JsonValueKind.Null,
            "terminal acknowledgement releases admission");
        // Keep this backend and its in-memory conversation owner alive while
        // the physical device boots again. No phone timeout or provider replay.
        boot = Guid.NewGuid().ToString("D");
        challenge = challenge with { BootId = boot, Challenge = Guid.NewGuid().ToString("D") };
        await device.PostAsJsonAsync("/v1/customer-sessions/device-challenge", challenge);
        await device.PostAsJsonAsync("/v1/customer-sessions/device-idle", new { bootId = boot });
        var secondTicket = await TicketAsync(first, firstProfile, Guid.NewGuid().ToString("D"));
        var secondCreated = await JsonAsync(await device.PostAsJsonAsync("/v1/customer-sessions/redeem", new CustomerRedemption(1,
            secondTicket.GetProperty("ticket").GetString()!, secondTicket.GetProperty("operationId").GetString()!, boot, challenge.Challenge)));
        var interrupted = secondCreated.GetProperty("customerSessionId").GetString()!;
        boot = Guid.NewGuid().ToString("D");
        challenge = challenge with { BootId = boot, Challenge = Guid.NewGuid().ToString("D") };
        await device.PostAsJsonAsync("/v1/customer-sessions/device-challenge", challenge);
        Check((await device.PostAsJsonAsync("/v1/customer-sessions/device-idle", new { bootId = boot })).IsSuccessStatusCode,
            "new boot idle closes the exact previous conversation while backend remains alive");
        Check(fixture.App.Services.GetRequiredService<OperationJournal>().CustomerSession(interrupted)!.State == "failed",
            "reboot marks prior customer session terminal without replay");
        var afterReboot = await TicketAsync(second, secondProfile, Guid.NewGuid().ToString("D"));
        Check((await device.PostAsJsonAsync("/v1/customer-sessions/redeem", new CustomerRedemption(1,
            afterReboot.GetProperty("ticket").GetString()!, afterReboot.GetProperty("operationId").GetString()!, boot, challenge.Challenge))).StatusCode == HttpStatusCode.Created,
            "new guest starts immediately after authenticated old-boot cleanup, without expiry delay");
        Console.WriteLine("PASS customer real HTTPS ticket issuance/redemption: role boundaries, IDOR, two phones, idempotence, consent and own end");

        async Task<JsonElement> TicketAsync(HttpClient client, JsonElement profile, string operation) =>
            await JsonAsync(await client.PostAsJsonAsync("/app/v1/customer/start-tickets", new
            {
                protocolVersion = 1, operationId = operation, deviceId = "granted-device", bootId = boot,
                challenge = challenge.Challenge, profileId = profile.GetProperty("profileId").GetString(),
                profileRevision = profile.GetProperty("revision").GetInt64()
            }));
    }

    private static async Task<JsonElement> CustomerAsync(HttpClient client, VirtualPasskey key)
    {
        await CsrfAsync(client);
        var options = await JsonAsync(await client.PostAsJsonAsync("/app/v1/customer/enrollment/options", new { displayName = "Same character" }));
        var credential = key.Create(options);
        Check((await client.PostAsJsonAsync("/app/v1/customer/enrollment/complete", new { credential })).IsSuccessStatusCode, "customer enrolls");
        await CsrfAsync(client);
        return (await JsonAsync(await client.PutAsJsonAsync("/app/v1/customer/character",
            new CharacterWriteRequest(0, new("Harry Potter", "Fictional fixture"))))).GetProperty("profile").Clone();
    }
}
