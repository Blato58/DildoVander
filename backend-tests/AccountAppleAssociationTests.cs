using System.Net;
using System.Net.Http.Json;

internal static partial class AccountIdentityTests
{
    private static async Task AppleAppSiteAssociationAsync()
    {
        await using var fixture = await Fixture.StartAsync();
        using var client = fixture.Client();
        var configuration = fixture.App.Configuration;
        const string path = "/.well-known/apple-app-site-association";
        const string legacy = "TEAM000001.com.example.installed";
        const string store = "TEAM000002.com.example.store";

        using var missing = await client.GetAsync(path);
        Check(missing.StatusCode == HttpStatusCode.NotFound, "AASA is unavailable without an explicit app identity");

        configuration["ApplicationIdentity:AppleAppId"] = legacy;
        var legacyResponse = await JsonAsync(await client.GetAsync(path));
        Check(legacyResponse.GetProperty("webcredentials").GetProperty("apps")[0].GetString() == legacy,
            "AASA retains the existing scalar configuration");

        configuration["ApplicationIdentity:AppleAppIds:0"] = store;
        configuration["ApplicationIdentity:AppleAppIds:1"] = legacy;
        using var multiple = await client.GetAsync(path);
        Check(multiple.StatusCode == HttpStatusCode.OK && multiple.Content.Headers.ContentType?.MediaType == "application/json",
            "AASA is public JSON without an account or CSRF token");
        var data = await multiple.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var ids = data.GetProperty("webcredentials").GetProperty("apps").EnumerateArray().Select(value => value.GetString()).ToArray();
        Check(ids.SequenceEqual(new[] { legacy, store }), "AASA authorizes both distinct explicit identities and preserves the installed app");

        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, path));
        Check(head.StatusCode == HttpStatusCode.OK && (await head.Content.ReadAsByteArrayAsync()).Length == 0,
            "AASA supports an unauthenticated HEAD without a response body");

        configuration["ApplicationIdentity:AppleAppId"] = null;
        using var listOnly = await client.GetAsync(path);
        Check(listOnly.StatusCode == HttpStatusCode.OK, "AASA supports an explicit app list without the legacy scalar");

        configuration["ApplicationIdentity:AppleAppIds:1"] = "TEAM000001.com.example.*";
        using var invalid = await client.GetAsync(path);
        Check(invalid.StatusCode == HttpStatusCode.NotFound, "AASA rejects the whole list when an identity is malformed or wildcarded");
    }
}
