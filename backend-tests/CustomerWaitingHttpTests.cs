using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

internal static partial class AccountIdentityTests
{
    internal static async Task RunCustomerWaitingHttpAsync()
    {
        await using var fixture = await Fixture.StartAsync(customerFeatures: true);
        using var owner = fixture.Client();
        using var customer = fixture.Client();
        using var anonymous = fixture.Client();
        using var otherDevice = fixture.Client();
        using var device = fixture.Client();
        using var ownerKey = new VirtualPasskey();
        using var customerKey = new VirtualPasskey();

        await EnrollAsync(owner, ownerKey, Fixture.Bootstrap, "Waiting owner");
        await CsrfAsync(owner);

        await CsrfAsync(customer);
        using (var optionsResponse = await customer.PostAsJsonAsync("/app/v1/customer/enrollment/options", new { displayName = "Waiting customer" }))
        {
            Check(optionsResponse.IsSuccessStatusCode, "waiting HTTPS customer enrollment options");
            var options = await JsonAsync(optionsResponse);
            using var complete = await customer.PostAsJsonAsync("/app/v1/customer/enrollment/complete",
                new { credential = customerKey.Create(options) });
            Check(complete.IsSuccessStatusCode, "waiting HTTPS customer account uses the real passkey enrollment route");
        }
        var identity = await CsrfAsync(customer);
        var customerId = identity.GetProperty("account").GetProperty("id").GetString()!;
        var profileResponse = await customer.PutAsJsonAsync("/app/v1/customer/character",
            new CharacterWriteRequest(0, new("Waiting character", "Fixture world")));
        var profileEnvelope = await JsonAsync(profileResponse);
        var profile = profileEnvelope.GetProperty("profile");
        var profileId = profile.GetProperty("profileId").GetString()!;
        var profileRevision = profile.GetProperty("revision").GetInt64();

        const string deviceId = "granted-device";
        var deviceCredential = "waiting-http-test-device-credential";
        var otherCredential = "waiting-http-test-other-device-credential";
        fixture.App.Configuration["Devices:0:CredentialSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceCredential)));
        fixture.App.Configuration["Devices:1:CredentialSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(otherCredential)));
        var access = fixture.App.Services.GetRequiredService<DeviceAccessStore>();
        var issuedDevice = access.IssueToken(access.VerifyCredential(deviceCredential)!)!;
        var issuedOtherDevice = access.IssueToken(access.VerifyCredential(otherCredential)!)!;
        device.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(issuedDevice.TokenType, issuedDevice.AccessToken);
        otherDevice.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(issuedOtherDevice.TokenType, issuedOtherDevice.AccessToken);

        string eventId;
        string eveningId;
        OperationJournal journal;
        using (var scope = fixture.App.Services.CreateScope())
        {
            journal = scope.ServiceProvider.GetRequiredService<OperationJournal>();
            var memory = scope.ServiceProvider.GetRequiredService<EveningMemoryStore>();
            var conversations = scope.ServiceProvider.GetRequiredService<ConversationStore>();
            var before = memory.GetStatus(deviceId);
            var created = conversations.CreateEvening(deviceId, Guid.NewGuid(), Encoding.UTF8.GetBytes("{\"test\":true}"),
                before.EveningId, "HTTPS waiting fixture");
            Check(created.Status == 201 && created.Response is not null, "fixture activates a real versioned evening before opening waiting");
            var active = memory.GetStatus(deviceId);
            eveningId = active.EveningId;
            var waitingScope = memory.ReadCustomerWaitingEvening(deviceId);
            Check(waitingScope.Open && waitingScope.EveningId == eveningId && waitingScope.ActivationRevision is > 0,
                "waiting fixture has a current durable evening activation");
            journal.SetCustomerMode(deviceId, true);
        }

        var boot = Guid.NewGuid().ToString("D");
        var challenge = "waiting-http-challenge-0001";
        var deviceChallenge = await DevicePostAsync(device, "/v1/customer-sessions/device-challenge",
            new CustomerDeviceChallenge(1, boot, challenge, 1, "fixture-catalog", null));
        Check(deviceChallenge.IsSuccessStatusCode, "device challenge is registered through the real bearer-authenticated HTTPS route");
        var observed = await device.GetAsync($"/v1/customer-sessions/device?bootId={Uri.EscapeDataString(boot)}&ready=true");
        Check(observed.IsSuccessStatusCode, "fresh authenticated runtime observation makes the waiting device available");

        const string customerWaiting = "/app/v1/customer/waiting/" + deviceId;
        using (var response = await anonymous.GetAsync(customerWaiting + "?account=" + Uri.EscapeDataString(customerId)))
        {
            Check(response.StatusCode == HttpStatusCode.Unauthorized, "anonymous and query-supplied account cannot read another account waiting state");
        }
        using (var response = await owner.GetAsync(customerWaiting))
        {
            Check(response.StatusCode == HttpStatusCode.OK, "owner cookie can read its privacy-filtered waiting projection");
        }
        using (var bearerRequest = new HttpRequestMessage(HttpMethod.Get, customerWaiting))
        {
            bearerRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-an-account-session");
            using var bearerResponse = await owner.SendAsync(bearerRequest);
            Check(bearerResponse.StatusCode == HttpStatusCode.Unauthorized,
                "a valid cookie cannot turn an arbitrary bearer header into account authority");
        }
        using (var response = await customer.GetAsync("/app/v1/customer/admin/devices/" + deviceId + "/waiting"))
        {
            Check(response.StatusCode == HttpStatusCode.Forbidden, "customer role cannot use operator waiting routes even with an account cookie");
        }
        using (var response = await anonymous.GetAsync(customerWaiting, new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token))
        {
            Check(response.StatusCode == HttpStatusCode.Unauthorized, "invalid anonymous bearer does not authenticate customer waiting");
        }

        var openRequestId = Guid.NewGuid().ToString("D");
        owner.DefaultRequestHeaders.Remove("X-DV-CSRF");
        using (var noCsrf = await owner.PostAsJsonAsync("/app/v1/customer/admin/devices/" + deviceId + "/waiting/open",
            new { schemaVersion = 1, requestId = openRequestId, previousEventId = (string?)null, expectedRevision = (long?)null }))
        {
            Check(noCsrf.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden, "operator waiting writes require CSRF");
        }
        await CsrfAsync(owner);
        owner.DefaultRequestHeaders.Remove("Origin");
        owner.DefaultRequestHeaders.Add("Origin", "https://wrong.example");
        using (var wrongOrigin = await owner.PostAsJsonAsync("/app/v1/customer/admin/devices/" + deviceId + "/waiting/open",
            new { schemaVersion = 1, requestId = openRequestId, previousEventId = (string?)null, expectedRevision = (long?)null }))
        {
            Check(wrongOrigin.StatusCode == HttpStatusCode.Forbidden, "operator waiting writes enforce the configured HTTPS origin");
        }
        owner.DefaultRequestHeaders.Remove("Origin");
        owner.DefaultRequestHeaders.Add("Origin", "https://localhost");
        await CsrfAsync(owner);
        var openBody = new { schemaVersion = 1, requestId = openRequestId, previousEventId = (string?)null, expectedRevision = (long?)null };
        var operatorView = await PostAsJsonWithCsrfAsync(owner,
            "/app/v1/customer/admin/devices/" + deviceId + "/waiting/open", openBody);
        var publicEvent = operatorView.GetProperty("event");
        eventId = publicEvent.GetProperty("id").GetString()!;
        Check(publicEvent.GetProperty("deviceId").GetString() == deviceId
            && publicEvent.GetProperty("eveningId").GetString() == eveningId
            && publicEvent.GetProperty("state").GetString() == "open"
            && publicEvent.GetProperty("revision").GetInt64() == 1
            && publicEvent.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(["deviceId", "eveningId", "id", "revision", "state"]),
            "operator wire event exposes only the five documented fields and its current activation");
        Check(operatorView.GetProperty("availability").GetProperty("state").GetString() == "available"
            && operatorView.GetProperty("availability").GetProperty("fresh").GetBoolean(),
            "available is tied to fresh authenticated runtime observation");

        var customerPath = "/app/v1/customer/waiting/" + deviceId;
        var badProof = await PostAsJsonWithCsrfAsync(customer, customerPath + "/join",
            new { schemaVersion = 1, requestId = Guid.NewGuid().ToString("D"), eventId, profileId, bootId = boot, challenge = "stale-challenge-000000" },
            HttpStatusCode.Conflict);
        _ = badProof;
        await PostAsJsonWithCsrfAsync(customer, customerPath + "/join",
            new { schemaVersion = 1, requestId = Guid.NewGuid().ToString("D"), eventId,
                profileId = Guid.NewGuid().ToString("D"), bootId = boot, challenge }, HttpStatusCode.Conflict);
        var joinId = Guid.NewGuid().ToString("D");
        var joinBody = new { schemaVersion = 1, requestId = joinId, eventId, profileId, bootId = boot, challenge };
        var ownView = await PostAsJsonWithCsrfAsync(customer, customerPath + "/join", joinBody);
        var ownEntry = ownView.GetProperty("entry");
        var entryId = ownEntry.GetProperty("id").GetString()!;
        Check(ownView.GetProperty("position").GetInt32() == 1 && ownView.GetProperty("count").GetInt32() == 1,
            "explicit join returns the customer's own queue position");
        Check(ownEntry.GetProperty("profileId").GetString() == profileId
            && !ownEntry.TryGetProperty("ownerId", out _)
            && !ownEntry.TryGetProperty("activationRevision", out _),
            "customer projection contains only its own profile binding and hides account/internal activation identifiers");
        var ownerView = await GetJsonAsync(await owner.GetAsync("/app/v1/customer/admin/devices/" + deviceId + "/waiting"));
        Check(ownerView.GetProperty("head").GetProperty("entryId").GetString() == entryId
            && !ownerView.GetProperty("head").TryGetProperty("ownerId", out _)
            && !ownerView.GetProperty("head").TryGetProperty("profileId", out _),
            "operator head contains no guest identity or character identifier");

        var paused = await PostAsJsonWithCsrfAsync(owner,
            "/app/v1/customer/admin/devices/" + deviceId + "/waiting/state",
            new { schemaVersion = 1, requestId = Guid.NewGuid().ToString("D"), eventId, expectedRevision = 2, state = "paused" });
        Check(paused.GetProperty("event").GetProperty("state").GetString() == "paused", "operator can pause the current event");
        await PostAsJsonWithCsrfAsync(customer, customerPath + "/join",
            new { schemaVersion = 1, requestId = Guid.NewGuid().ToString("D"), eventId, profileId, bootId = boot, challenge },
            HttpStatusCode.Conflict);
        var acceptedReplay = await PostAsJsonWithCsrfAsync(customer, customerPath + "/join", joinBody);
        Check(acceptedReplay.GetProperty("entry").GetProperty("id").GetString() == entryId
            && acceptedReplay.GetProperty("availability").GetProperty("state").GetString() == "paused",
            "accepted join replays idempotently while returning the fresh paused state");
        var reopened = await PostAsJsonWithCsrfAsync(owner,
            "/app/v1/customer/admin/devices/" + deviceId + "/waiting/state",
            new { schemaVersion = 1, requestId = Guid.NewGuid().ToString("D"), eventId, expectedRevision = 3, state = "open" });
        Check(reopened.GetProperty("event").GetProperty("state").GetString() == "open", "operator explicitly reopens without creating another event");

        var oldProofClaim = await PostAsJsonWithCsrfAsync(customer, customerPath + "/claim",
            new { schemaVersion = 1, requestId = Guid.NewGuid().ToString("D"), eventId, entryId,
                expectedRevision = ownEntry.GetProperty("revision").GetInt64(), bootId = boot, challenge = "stale-challenge-000000" },
            HttpStatusCode.Conflict);
        _ = oldProofClaim;
        await PostAsJsonWithCsrfAsync(customer, customerPath + "/claim",
            new { schemaVersion = 1, requestId = Guid.NewGuid().ToString("D"), eventId, entryId,
                expectedRevision = ownEntry.GetProperty("revision").GetInt64(), bootId = Guid.NewGuid().ToString("D"), challenge },
            HttpStatusCode.Conflict);
        var claimId = Guid.NewGuid().ToString("D");
        var claimChallenge = "waiting-http-challenge-0002";
        using (var newChallenge = await DevicePostAsync(device, "/v1/customer-sessions/device-challenge",
            new CustomerDeviceChallenge(1, boot, claimChallenge, 1, "fixture-catalog", null)))
        {
            Check(newChallenge.IsSuccessStatusCode, "runtime can rotate proximity challenge without changing waiting command identity");
        }
        using (var ready = await device.GetAsync($"/v1/customer-sessions/device?bootId={Uri.EscapeDataString(boot)}&ready=true"))
        { Check(ready.IsSuccessStatusCode, "fresh current challenge has an authenticated ready observation"); }
        var claimBody = new { schemaVersion = 1, requestId = claimId, eventId, entryId,
            expectedRevision = ownEntry.GetProperty("revision").GetInt64(), bootId = boot, challenge = claimChallenge };
        var claimed = await PostAsJsonWithCsrfAsync(customer, customerPath + "/claim", claimBody);
        var claim = claimed.GetProperty("entry").GetProperty("claim");
        var operationId = claim.GetProperty("id").GetString()!;
        Check(claimed.GetProperty("position").GetInt32() == 1 && claim.GetProperty("expiresAt").GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            "only a fresh available queue head receives a bounded claim");
        var retryChallenge = "waiting-http-challenge-0003";
        using (var newChallenge = await DevicePostAsync(device, "/v1/customer-sessions/device-challenge",
            new CustomerDeviceChallenge(1, boot, retryChallenge, 1, "fixture-catalog", null)))
        { Check(newChallenge.IsSuccessStatusCode, "claim replay receives a fresh current challenge"); }
        using (var ready = await device.GetAsync($"/v1/customer-sessions/device?bootId={Uri.EscapeDataString(boot)}&ready=true"))
        { Check(ready.IsSuccessStatusCode, "fresh challenge runtime observation accepted"); }
        var replayedClaim = await PostAsJsonWithCsrfAsync(customer, customerPath + "/claim",
            new { schemaVersion = 1, requestId = claimId, eventId, entryId,
                expectedRevision = ownEntry.GetProperty("revision").GetInt64(), bootId = boot, challenge = retryChallenge });
        Check(replayedClaim.GetProperty("entry").GetProperty("claim").GetProperty("id").GetString() == operationId,
            "claim replay preserves its original operation ID while requiring fresh proximity");

        // A real account ticket is bound to the claim ID. Only the matching provisioned device can redeem it.
        var ticketRequest = new CustomerTicketRequest(1, operationId, deviceId, boot, retryChallenge,
            profileId, profileRevision, PersonalMemoryConsent: false, OpenAiConsent: true);
        var ticketResponse = await PostAsJsonWithCsrfAsync(customer, "/app/v1/customer/start-tickets", ticketRequest);
        Check(ticketResponse.GetProperty("operationId").GetString() == operationId,
            "start ticket operation ID is the exact claim identifier");
        var ticket = ticketResponse.GetProperty("ticket").GetString()!;
        using (var stolen = await otherDevice.PostAsJsonAsync("/v1/customer-sessions/redeem",
            new CustomerRedemption(1, ticket, operationId, boot, retryChallenge)))
        {
            Check(stolen.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict,
                "a second provisioned phone cannot redeem another device's ticket");
        }
        using var redeemed = await DevicePostAsync(device, "/v1/customer-sessions/redeem",
            new CustomerRedemption(1, ticket, operationId, boot, retryChallenge));
        Check(redeemed.StatusCode == HttpStatusCode.Created, "the original device redeems the claimed ticket through HTTPS");
        var redeemedBody = await GetJsonAsync(redeemed);
        var sessionId = redeemedBody.GetProperty("customerSessionId").GetString()!;
        var sessions = await GetJsonAsync(await customer.GetAsync("/app/v1/customer/sessions"));
        Check(sessions.GetProperty("items").EnumerateArray().Any(item => item.GetProperty("id").GetString() == sessionId),
            "ticket redemption persists a real account-bound customer session");

        var editedProfile = await PutCharacterWithCsrfAsync(customer,
            new CharacterWriteRequest(profileRevision, new("Edited while session is active", "Fixture world"), profileId));
        var editedRevision = editedProfile.GetProperty("profile").GetProperty("revision").GetInt64();
        var issuedReplay = await PostAsJsonWithCsrfAsync(customer, "/app/v1/customer/start-tickets", ticketRequest);
        Check(issuedReplay.GetProperty("ticket").GetString() == ticket
            && issuedReplay.GetProperty("sessionId").GetString() == sessionId,
            "same immutable issuer request replays its original ticket after an ordinary profile edit");
        await PostAsJsonWithCsrfAsync(customer, "/app/v1/customer/start-tickets",
            ticketRequest with { ProfileRevision = editedRevision }, HttpStatusCode.Conflict);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.App.Configuration["Backend:DatabasePath"]
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE customer_start_tickets SET expires_at=0 WHERE operation_id=$operation";
            command.Parameters.AddWithValue("$operation", operationId);
            Check(await command.ExecuteNonQueryAsync() == 1, "test fixture advances only the accepted ticket's persisted expiry");
        }
        using (var replay = await DevicePostAsync(device, "/v1/customer-sessions/redeem",
            new CustomerRedemption(1, ticket, operationId, boot, retryChallenge)))
        {
            Check(replay.StatusCode == HttpStatusCode.Created, "accepted ticket replay survives ordinary character revision and persisted expiry");
            var replayBody = await GetJsonAsync(replay);
            Check(replayBody.GetProperty("customerSessionId").GetString() == sessionId,
                "expired accepted ticket replay keeps its original session, snapshot and operation context");
        }

        using var appScope = fixture.App.Services.CreateScope();
        var authoritativeJournal = appScope.ServiceProvider.GetRequiredService<OperationJournal>();
        Check(authoritativeJournal.ActiveCustomerSession(deviceId)?.Id == sessionId,
            "redeemed claim owns the active device lease before any runtime completion report");

        using (var deletedProfile = await DeleteCharacterWithCsrfAsync(customer,
            $"/app/v1/customer/character?expectedRevision={editedRevision}&expectedProfileId={Uri.EscapeDataString(profileId)}"))
        { Check(deletedProfile.StatusCode == HttpStatusCode.NoContent, "explicit profile deletion is applied through the account endpoint"); }
        await PostAsJsonWithCsrfAsync(customer, "/app/v1/customer/start-tickets", ticketRequest, HttpStatusCode.Gone);
        Check(authoritativeJournal.ActiveCustomerSession(deviceId) is { Id: var activeId, EndRequested: true } && activeId == sessionId,
            "profile deletion requests runtime reconciliation without releasing the accepted lease");
        var end = await PostAsJsonWithCsrfAsync(customer, "/app/v1/customer/sessions/" + sessionId + "/end",
            new { }, HttpStatusCode.Accepted);
        Check(end.GetProperty("state").GetString() == "ending", "account Stop remains request-only and available after profile deletion");
        var conversationId = redeemedBody.GetProperty("conversationId").GetString()!;
        using (var idle = await DevicePostAsync(device, $"/v1/customer-sessions/{sessionId}/device-state",
            new CustomerDeviceState(boot, "idle", conversationId)))
        { Check(idle.IsSuccessStatusCode, "runtime idle report remains available after account Stop/profile deletion"); }
        Check(authoritativeJournal.ActiveCustomerSession(deviceId) is null,
            "only the matching runtime idle report releases the deleted profile's accepted lease");

        // A second account demonstrates that a deleted, unredeemed profile cannot
        // turn an already-issued waiting claim into a device session.
        using var secondCustomer = fixture.Client();
        using var secondCustomerKey = new VirtualPasskey();
        await CsrfAsync(secondCustomer);
        using (var optionsResponse = await secondCustomer.PostAsJsonAsync("/app/v1/customer/enrollment/options", new { displayName = "Second waiting customer" }))
        {
            var options = await JsonAsync(optionsResponse);
            using var complete = await secondCustomer.PostAsJsonAsync("/app/v1/customer/enrollment/complete",
                new { credential = secondCustomerKey.Create(options) });
            Check(complete.IsSuccessStatusCode, "second HTTP customer account is independently passkey enrolled");
        }
        await CsrfAsync(secondCustomer);
        var secondProfileEnvelope = await PutCharacterWithCsrfAsync(secondCustomer,
            new CharacterWriteRequest(0, new("Second waiting character", "Fixture world")));
        var secondProfile = secondProfileEnvelope.GetProperty("profile");
        var secondProfileId = secondProfile.GetProperty("profileId").GetString()!;
        var secondProfileRevision = secondProfile.GetProperty("revision").GetInt64();
        var secondBoot = Guid.NewGuid().ToString("D");
        var secondChallenge = "waiting-http-challenge-0004";
        using (var challengeResponse = await DevicePostAsync(device, "/v1/customer-sessions/device-challenge",
            new CustomerDeviceChallenge(1, secondBoot, secondChallenge, 2, "fixture-catalog", null)))
        { Check(challengeResponse.IsSuccessStatusCode, "device creates a fresh boot challenge after terminal idle reconciliation"); }
        using (var ready = await device.GetAsync($"/v1/customer-sessions/device?bootId={Uri.EscapeDataString(secondBoot)}&ready=true"))
        { Check(ready.IsSuccessStatusCode, "new boot has fresh runtime availability"); }
        var secondJoinId = Guid.NewGuid().ToString("D");
        var secondJoin = await PostAsJsonWithCsrfAsync(secondCustomer, customerPath + "/join",
            new { schemaVersion = 1, requestId = secondJoinId, eventId, profileId = secondProfileId,
                bootId = secondBoot, challenge = secondChallenge });
        var secondEntry = secondJoin.GetProperty("entry");
        var secondEntryId = secondEntry.GetProperty("id").GetString()!;
        var secondClaimId = Guid.NewGuid().ToString("D");
        var secondClaim = await PostAsJsonWithCsrfAsync(secondCustomer, customerPath + "/claim",
            new { schemaVersion = 1, requestId = secondClaimId, eventId, entryId = secondEntryId,
                expectedRevision = secondEntry.GetProperty("revision").GetInt64(), bootId = secondBoot, challenge = secondChallenge });
        var secondOperationId = secondClaim.GetProperty("entry").GetProperty("claim").GetProperty("id").GetString()!;
        var secondTicketEnvelope = await PostAsJsonWithCsrfAsync(secondCustomer, "/app/v1/customer/start-tickets",
            new CustomerTicketRequest(1, secondOperationId, deviceId, secondBoot, secondChallenge,
                secondProfileId, secondProfileRevision, PersonalMemoryConsent: false, OpenAiConsent: true));
        var secondTicket = secondTicketEnvelope.GetProperty("ticket").GetString()!;
        using (var deletedBeforeRedeem = await DeleteCharacterWithCsrfAsync(secondCustomer,
            $"/app/v1/customer/character?expectedRevision={secondProfileRevision}&expectedProfileId={Uri.EscapeDataString(secondProfileId)}"))
        { Check(deletedBeforeRedeem.StatusCode == HttpStatusCode.NoContent, "unredeemed profile deletion revokes its waiting claim and ticket"); }
        using (var deniedRedemption = await DevicePostAsync(device, "/v1/customer-sessions/redeem",
            new CustomerRedemption(1, secondTicket, secondOperationId, secondBoot, secondChallenge)))
        {
            Check(deniedRedemption.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Gone or HttpStatusCode.NotFound,
                "deleted-profile ticket cannot create a customer session before journal redemption");
        }
        Check(authoritativeJournal.ActiveCustomerSession(deviceId) is null,
            "pre-redemption profile deletion cannot acquire the device lease");

        var beforeActivationChange = await GetJsonAsync(await owner.GetAsync("/app/v1/customer/admin/devices/" + deviceId + "/waiting"));
        var beforeActivationEvent = beforeActivationChange.GetProperty("event");
        Check(beforeActivationEvent.GetProperty("id").GetString() == eventId,
            "operator read identifies the durable event before activation changes");
        var eventRevision = beforeActivationEvent.GetProperty("revision").GetInt64();
        string secondEveningId;
        using (var scope = fixture.App.Services.CreateScope())
        {
            var memory = scope.ServiceProvider.GetRequiredService<EveningMemoryStore>();
            var conversations = scope.ServiceProvider.GetRequiredService<ConversationStore>();
            var current = memory.GetStatus(deviceId);
            var created = conversations.CreateEvening(deviceId, Guid.NewGuid(), Encoding.UTF8.GetBytes("{\"turn\":\"B\"}"),
                current.EveningId, "Waiting activation B");
            Check(created.Status == 201 && created.Response is not null, "test activates the next genuine evening through ConversationStore");
            secondEveningId = memory.GetStatus(deviceId).EveningId;
        }
        var eventOnB = await PostAsJsonWithCsrfAsync(owner,
            "/app/v1/customer/admin/devices/" + deviceId + "/waiting/open",
            new { schemaVersion = 1, requestId = Guid.NewGuid().ToString("D"), previousEventId = eventId, expectedRevision = eventRevision });
        var eventBId = eventOnB.GetProperty("event").GetProperty("id").GetString()!;
        Check(eventBId != eventId && eventOnB.GetProperty("event").GetProperty("eveningId").GetString() == secondEveningId,
            "new event replaces the old activation-bound event on evening B");
        var eventBRevision = eventOnB.GetProperty("event").GetProperty("revision").GetInt64();
        using (var scope = fixture.App.Services.CreateScope())
        {
            var memory = scope.ServiceProvider.GetRequiredService<EveningMemoryStore>();
            var conversations = scope.ServiceProvider.GetRequiredService<ConversationStore>();
            var activatedBack = conversations.ActivateEvening(deviceId, Guid.NewGuid(), Encoding.UTF8.GetBytes("{\"turn\":\"A\"}"),
                eveningId, secondEveningId);
            Check(activatedBack.Status == 200 && memory.GetStatus(deviceId).EveningId == eveningId,
                "test reactivates the original evening after A-to-B rotation");
        }
        var eventBackOnA = await PostAsJsonWithCsrfAsync(owner,
            "/app/v1/customer/admin/devices/" + deviceId + "/waiting/open",
            new { schemaVersion = 1, requestId = Guid.NewGuid().ToString("D"), previousEventId = eventBId, expectedRevision = eventBRevision });
        var eventBackOnAId = eventBackOnA.GetProperty("event").GetProperty("id").GetString()!;
        Check(eventBackOnAId != eventBId && eventBackOnA.GetProperty("event").GetProperty("eveningId").GetString() == eveningId,
            "A-to-B-to-A requires a new event bound to the newly advanced A activation");
        var replacementProfileEnvelope = await PutCharacterWithCsrfAsync(customer,
            new CharacterWriteRequest(0, new("Replacement character", "Fixture world")));
        var replacementProfileId = replacementProfileEnvelope.GetProperty("profile").GetProperty("profileId").GetString()!;
        await PostAsJsonWithCsrfAsync(customer, customerPath + "/join",
            new { schemaVersion = 1, requestId = Guid.NewGuid().ToString("D"), eventId, profileId = replacementProfileId,
                bootId = secondBoot, challenge = secondChallenge }, HttpStatusCode.Conflict);

        var beforeClose = await GetJsonAsync(await owner.GetAsync("/app/v1/customer/admin/devices/" + deviceId + "/waiting"));
        var beforeCloseRevision = beforeClose.GetProperty("event").GetProperty("revision").GetInt64();
        var closed = await PostAsJsonWithCsrfAsync(owner,
            "/app/v1/customer/admin/devices/" + deviceId + "/waiting/state",
            new { schemaVersion = 1, requestId = Guid.NewGuid().ToString("D"), eventId = eventBackOnAId, expectedRevision = beforeCloseRevision, state = "closed" });
        Check(closed.GetProperty("event").GetProperty("state").GetString() == "closed",
            "operator can close the current event after revoked pending admission");
        var staleOpenReplay = await PostAsJsonWithCsrfAsync(owner,
            "/app/v1/customer/admin/devices/" + deviceId + "/waiting/open", openBody);
        Check(staleOpenReplay.GetProperty("event").GetProperty("id").GetString() == eventBackOnAId
            && staleOpenReplay.GetProperty("event").GetProperty("state").GetString() == "closed"
            && staleOpenReplay.GetProperty("event").GetProperty("revision").GetInt64() == beforeCloseRevision + 1,
            "accepted old-open replay after A-to-B-to-A returns the fresh closed projection without reopening its original event");
        Console.WriteLine("Customer waiting real HTTPS ownership, idempotency, privacy, proximity and claim-bound redemption checks passed.");
    }

    private static async Task<HttpResponseMessage> DevicePostAsync(HttpClient client, string path, object body)
        => await client.PostAsJsonAsync(path, body);

    private static async Task<JsonElement> PutCharacterWithCsrfAsync(HttpClient client, CharacterWriteRequest request)
    {
        await CsrfAsync(client);
        using var response = await client.PutAsJsonAsync("/app/v1/customer/character", request);
        return await GetJsonAsync(response);
    }

    private static async Task<HttpResponseMessage> DeleteCharacterWithCsrfAsync(HttpClient client, string path)
    {
        await CsrfAsync(client);
        return await client.DeleteAsync(path);
    }

    private static async Task<JsonElement> PostAsJsonWithCsrfAsync(HttpClient client, string path, object body,
        HttpStatusCode expected = HttpStatusCode.OK)
    {
        await CsrfAsync(client);
        using var response = await client.PostAsJsonAsync(path, body);
        if (response.StatusCode != expected)
        {
            throw new InvalidOperationException($"Expected HTTP {(int)expected}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
        return response.IsSuccessStatusCode ? await GetJsonAsync(response) : default;
    }

    private static async Task<JsonElement> GetJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Expected success, got HTTP {(int)response.StatusCode}: {body}");
        }
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
