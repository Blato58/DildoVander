using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Customers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

internal static partial class AccountIdentityTests
{
    internal static async Task RunCustomerConversationPreferenceHttpAsync()
    {
        await using var fixture = await Fixture.StartAsync(
            customerFeatures: true,
            configureEndpoints: app => app.MapCustomerConversationPreferences());
        using var customerA = fixture.Client();
        using var customerB = fixture.Client();
        using var anonymous = fixture.Client();
        using var owner = fixture.Client();
        using var ownerKey = new VirtualPasskey();
        using var customerKeyA = new VirtualPasskey();
        using var customerKeyB = new VirtualPasskey();

        await EnrollAsync(owner, ownerKey, Fixture.Bootstrap, "Preference owner");
        var accountA = await EnrollCustomerAsync(customerA, customerKeyA, "Preference A");
        var profileA = await SaveProfileAsync(customerA, 0, null, "First character");
        var route = "/app/v1/customer/conversation-preferences";
        var defaults = await JsonAsync(await customerA.GetAsync(route));
        Check(defaults.GetProperty("profileId").GetString() == profileA.ProfileId
            && defaults.GetProperty("profileRevision").GetInt64() == profileA.Revision
            && defaults.GetProperty("preferenceRevision").GetInt64() == 0
            && defaults.GetProperty("conversationLanguage").GetString() == "cs-CZ"
            && defaults.GetProperty("tone").GetString() == "roast"
            && !defaults.GetProperty("persisted").GetBoolean(),
            "first preference GET returns defaults bound to the current owned profile");
        Check(customerA.DefaultRequestHeaders.Contains("X-DV-CSRF"), "customer session has a CSRF token for preference mutations");
        using (var noWriteConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.App.Services.GetRequiredService<CharacterProfileStore>().CustomerDataDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString()))
        {
            noWriteConnection.Open();
            using var query = noWriteConnection.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM customer_conversation_preferences WHERE account_id=$account";
            query.Parameters.AddWithValue("$account", accountA);
            Check((long)query.ExecuteScalar()! == 0, "reading default preferences does not create a durable preference row");
        }

        var save = PreferenceBody(profileA, 0, "en-US", "gentle");
        var saved = await JsonAsync(await customerA.PutAsJsonAsync(route, save));
        Check(saved.GetProperty("preferenceRevision").GetInt64() == 1
            && saved.GetProperty("persisted").GetBoolean()
            && saved.GetProperty("conversationLanguage").GetString() == "en-US"
            && saved.GetProperty("tone").GetString() == "gentle",
            "explicit preference write advances its revision and persists both choices");

        using var resumedSession = fixture.Client();
        await SignInAsync(resumedSession, customerKeyA);
        await CsrfAsync(resumedSession);
        var afterSignIn = await JsonAsync(await resumedSession.GetAsync(route));
        Check(afterSignIn.GetProperty("persisted").GetBoolean()
            && afterSignIn.GetProperty("preferenceRevision").GetInt64() == 1
            && afterSignIn.GetProperty("conversationLanguage").GetString() == "en-US"
            && afterSignIn.GetProperty("tone").GetString() == "gentle",
            "saved preferences survive a fresh authenticated session");

        var editedProfile = await SaveProfileAsync(customerA, profileA.Revision, profileA.ProfileId, "Corrected character");
        var afterProfileEdit = await JsonAsync(await customerA.GetAsync(route));
        Check(editedProfile.ProfileId == profileA.ProfileId && editedProfile.Revision == profileA.Revision + 1
            && afterProfileEdit.GetProperty("profileRevision").GetInt64() == editedProfile.Revision
            && afterProfileEdit.GetProperty("preferenceRevision").GetInt64() == 1
            && afterProfileEdit.GetProperty("conversationLanguage").GetString() == "en-US"
            && afterProfileEdit.GetProperty("tone").GetString() == "gentle",
            "editing the same character preserves its conversation preferences");
        await ExpectProblemAsync(await customerA.PutAsJsonAsync(route,
            PreferenceBody(editedProfile, 0, "cs-CZ", "roast")), HttpStatusCode.Conflict,
            "conversation_preferences_revision_conflict", "stale preference compare-and-swap is rejected");

        using (var forceMaxRevision = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.App.Services.GetRequiredService<CharacterProfileStore>().CustomerDataDatabasePath,
        }.ToString()))
        {
            forceMaxRevision.Open();
            using var update = forceMaxRevision.CreateCommand();
            update.CommandText = "UPDATE customer_conversation_preferences SET revision=$revision WHERE account_id=$account";
            update.Parameters.AddWithValue("$revision", long.MaxValue);
            update.Parameters.AddWithValue("$account", accountA);
            Check(update.ExecuteNonQuery() == 1, "fixture advances stored preference revision to its integer limit");
        }
        await ExpectProblemAsync(await customerA.PutAsJsonAsync(route,
            PreferenceBody(editedProfile, long.MaxValue, "cs-CZ", "roast")), HttpStatusCode.Conflict,
            "conversation_preferences_revision_conflict", "preference revision cannot overflow its durable integer");

        var accountB = await EnrollCustomerAsync(customerB, customerKeyB, "Preference B");
        _ = accountB;
        var profileB = await SaveProfileAsync(customerB, 0, null, "Second character");
        var defaultsB = await JsonAsync(await customerB.GetAsync(route));
        Check(!defaultsB.GetProperty("persisted").GetBoolean()
            && defaultsB.GetProperty("preferenceRevision").GetInt64() == 0
            && defaultsB.GetProperty("tone").GetString() == "roast",
            "another account receives only its own default preference state");
        await ExpectProblemAsync(await customerB.PutAsJsonAsync(route,
            PreferenceBody(profileA, 0, "en-US", "cheeky")), HttpStatusCode.Conflict,
            "character_revision_mismatch", "another account cannot attach preferences to a foreign profile");

        var replacement = await SaveProfileAsync(customerA, editedProfile.Revision, editedProfile.ProfileId,
            "Replacement character", newCharacter: true);
        Check(replacement.ProfileId != editedProfile.ProfileId && replacement.Revision == 1,
            "explicit new-character save creates a distinct profile identity");
        var replacementDefaults = await JsonAsync(await customerA.GetAsync(route));
        Check(replacementDefaults.GetProperty("profileId").GetString() == replacement.ProfileId
            && replacementDefaults.GetProperty("preferenceRevision").GetInt64() == 0
            && replacementDefaults.GetProperty("conversationLanguage").GetString() == "cs-CZ"
            && replacementDefaults.GetProperty("tone").GetString() == "roast"
            && !replacementDefaults.GetProperty("persisted").GetBoolean(),
            "replacement profile starts with defaults after old-profile preferences are cleared");
        await ExpectProblemAsync(await customerA.PutAsJsonAsync(route,
            PreferenceBody(editedProfile, long.MaxValue, "en-US", "gentle")), HttpStatusCode.Conflict,
            "character_revision_mismatch", "preferences cannot be restored through a replaced profile identity");

        using var cacheResponse = await customerA.GetAsync(route);
        Check(cacheResponse.Headers.CacheControl?.NoStore == true, "preference reads are not stored by HTTP caches");
        await ExpectProblemAsync(await anonymous.GetAsync(route), HttpStatusCode.Unauthorized,
            "account_required", "anonymous preference read is denied");

        customerA.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-session");
        await ExpectProblemAsync(await customerA.GetAsync(route), HttpStatusCode.Unauthorized,
            "account_required", "preference route rejects an authorization bearer header even with a session cookie");
        customerA.DefaultRequestHeaders.Authorization = null;

        await ExpectProblemAsync(await customerA.GetAsync(route + "?profileId=" + replacement.ProfileId), HttpStatusCode.BadRequest,
            "conversation_preferences_invalid", "preference route rejects query-based profile selection");
        await ExpectProblemAsync(await customerA.PutAsJsonAsync(route,
            new { schemaVersion = 1, profileId = replacement.ProfileId, profileRevision = 1,
                expectedPreferenceRevision = 0, conversationLanguage = "fr-FR", tone = "gentle" }),
            HttpStatusCode.BadRequest, "conversation_preferences_invalid", "unsupported conversation language is rejected");
        await ExpectProblemAsync(await customerA.PutAsJsonAsync(route,
            new { schemaVersion = 1, profileId = replacement.ProfileId, profileRevision = 1,
                expectedPreferenceRevision = 0, conversationLanguage = "en-US", tone = "host-override" }),
            HttpStatusCode.BadRequest, "conversation_preferences_invalid", "unknown tone cannot expand policy");
        await ExpectProblemAsync(await customerA.PutAsJsonAsync(route,
            new { schemaVersion = 1, profileId = replacement.ProfileId, profileRevision = 1,
                expectedPreferenceRevision = 0, conversationLanguage = "en-US", tone = "gentle", extra = "ignored" }),
            HttpStatusCode.BadRequest, "conversation_preferences_invalid", "unknown request fields are rejected");

        var csrfToken = customerA.DefaultRequestHeaders.GetValues("X-DV-CSRF").Single();
        customerA.DefaultRequestHeaders.Remove("X-DV-CSRF");
        try
        {
            await ExpectProblemAsync(await customerA.PutAsJsonAsync(route,
                PreferenceBody(replacement, 0, "en-US", "gentle")), HttpStatusCode.BadRequest,
                "csrf_required", "preference mutation requires the session CSRF token");
        }
        finally { customerA.DefaultRequestHeaders.Add("X-DV-CSRF", csrfToken); }

        var origin = customerA.DefaultRequestHeaders.GetValues("Origin").Single();
        customerA.DefaultRequestHeaders.Remove("Origin");
        customerA.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
        try
        {
            await ExpectProblemAsync(await customerA.PutAsJsonAsync(route,
                PreferenceBody(replacement, 0, "en-US", "gentle")), HttpStatusCode.Forbidden,
                "account_origin_required", "preference mutation rejects an untrusted origin");
        }
        finally
        {
            customerA.DefaultRequestHeaders.Remove("Origin");
            customerA.DefaultRequestHeaders.Add("Origin", origin);
        }

        using var oversized = new HttpRequestMessage(HttpMethod.Put, route)
        {
            Content = new StringContent("{" + new string(' ', 2048) + "}", Encoding.UTF8, "application/json"),
        };
        await ExpectProblemAsync(await customerA.SendAsync(oversized), HttpStatusCode.RequestEntityTooLarge,
            "request_too_large", "oversized preference JSON is rejected before parsing");
    }

    private static async Task<string> EnrollCustomerAsync(HttpClient client, VirtualPasskey key, string displayName)
    {
        await CsrfAsync(client);
        var options = await JsonAsync(await client.PostAsJsonAsync(
            "/app/v1/customer/enrollment/options", new { displayName }));
        using var completion = await client.PostAsJsonAsync(
            "/app/v1/customer/enrollment/complete", new { credential = key.Create(options) });
        Check(completion.IsSuccessStatusCode, "customer preference fixture enrolls using a real passkey");
        var session = await CsrfAsync(client);
        return session.GetProperty("account").GetProperty("id").GetString()!;
    }

    private static async Task<CharacterProfile> SaveProfileAsync(HttpClient client, long expectedRevision,
        string? expectedProfileId, string name, bool newCharacter = false)
    {
        using var response = await client.PutAsJsonAsync("/app/v1/customer/character",
            new CharacterWriteRequest(expectedRevision, new CharacterUserInput(name, "Conversation preferences"),
                expectedProfileId, newCharacter, OpenAiConsent: false));
        var envelope = await JsonAsync(response);
        return JsonSerializer.Deserialize<CharacterProfile>(envelope.GetProperty("profile").GetRawText(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static object PreferenceBody(CharacterProfile profile, long expectedPreferenceRevision,
        string language, string tone) => new
        {
            schemaVersion = 1,
            profileId = profile.ProfileId,
            profileRevision = profile.Revision,
            expectedPreferenceRevision,
            conversationLanguage = language,
            tone,
        };

    private static async Task ExpectProblemAsync(HttpResponseMessage response, HttpStatusCode status,
        string code, string description)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Check(response.StatusCode == status, $"{description}: expected {(int)status}, got {(int)response.StatusCode}: {text}");
            using var document = JsonDocument.Parse(text);
            Check(document.RootElement.GetProperty("code").GetString() == code,
                $"{description}: expected code {code}");
        }
    }
}
