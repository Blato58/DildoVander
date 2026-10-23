using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Memory;
using Kufr.Backend.Features.Tonight;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;

internal static class TonightMemoryStatusTests
{
    private const string DeviceId = "tonight-status-device";
    private const string OtherDeviceId = "tonight-status-other-device";
    private const string Endpoint = "/app/v1/tonight/devices/" + DeviceId + "/memory-status";
    private const string LegacyDeviceSecret = "fixture-legacy-device-secret";

    internal static async Task RunAsync()
    {
        VerifySafeStatusProjection();
        await VerifyAuthenticatedHttpContractAsync();
        await VerifyDisabledAccountMappingAsync();
        Console.WriteLine("Tonight memory status: HTTPS account authorization, current grants/sessions, safe response contract and disabled mapping passed.");
    }

    private static void VerifySafeStatusProjection()
    {
        var unknownError = new MemoryProviderStatus("provider-secret", true, false, "configured", null,
            3, 4, 5, 6_000, 10_000, 7, "warning", true,
            "fixture_sensitive_details", DateTimeOffset.Parse("2026-10-23T10:00:00Z"));
        var projected = TonightMemoryStatus.From(unknownError);
        Check(projected.LastStorageErrorCode is null && projected.LastStorageErrorAt is null,
            "unknown storage error details are omitted from the public response");

        var knownError = TonightMemoryStatus.From(unknownError with
        {
            LastStorageErrorCode = "archive_capacity"
        });
        Check(knownError.LastStorageErrorCode == "archive_capacity" && knownError.LastStorageErrorAt is not null,
            "known archive capacity diagnostics retain their safe code and time");
    }

    private static async Task VerifyAuthenticatedHttpContractAsync()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.SeedFullArchiveAsync();

        using (var anonymous = fixture.Client())
        using (var response = await anonymous.GetAsync(Endpoint))
        {
            Check(response.StatusCode == HttpStatusCode.Unauthorized,
                "anonymous request is challenged by the real account cookie pipeline");
        }

        using (var legacyOnly = fixture.Client())
        {
            legacyOnly.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", LegacyDeviceSecret);
            using var response = await legacyOnly.GetAsync(Endpoint);
            Check(response.StatusCode == HttpStatusCode.Unauthorized,
                "legacy device bearer alone cannot authenticate this account route");
        }

        using var operatorClient = await fixture.CreateAccountClientAsync(AccountRoles.Operator,
            [(DeviceId, "operate")]);
        using (var response = await operatorClient.Client.GetAsync(Endpoint))
        {
            await VerifyFullResponseAsync(response);
        }

        operatorClient.Client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", LegacyDeviceSecret);
        using (var mixedCredentialResponse = await operatorClient.Client.GetAsync(Endpoint))
        {
            Check(mixedCredentialResponse.StatusCode == HttpStatusCode.Unauthorized,
                "a legacy Authorization header is rejected even when an account cookie is present");
            VerifySecurityHeaders(mixedCredentialResponse);
        }
        operatorClient.Client.DefaultRequestHeaders.Authorization = null;

        using (var response = await operatorClient.Client.GetAsync(
            $"/app/v1/tonight/devices/{OtherDeviceId}/memory-status"))
        {
            Check(response.StatusCode == HttpStatusCode.NotFound,
                "operator grant for one device cannot disclose another configured device");
            VerifySecurityHeaders(response);
        }
        using (var response = await operatorClient.Client.GetAsync(
            "/app/v1/tonight/devices/unconfigured-device/memory-status"))
        {
            Check(response.StatusCode == HttpStatusCode.NotFound,
                "unconfigured device is concealed even from an operator");
            VerifySecurityHeaders(response);
        }

        using var readerClient = await fixture.CreateAccountClientAsync(AccountRoles.Operator,
            [(DeviceId, "read")]);
        using (var response = await readerClient.Client.GetAsync(Endpoint))
        {
            Check(response.StatusCode == HttpStatusCode.NotFound,
                "operator read-only grant cannot inspect memory status");
            VerifySecurityHeaders(response);
        }

        using var managerClient = await fixture.CreateAccountClientAsync(AccountRoles.Operator,
            [(DeviceId, "manage")]);
        using (var response = await managerClient.Client.GetAsync(Endpoint))
        {
            Check(response.StatusCode == HttpStatusCode.OK,
                "operator with manage grant may inspect the configured device");
            VerifySecurityHeaders(response);
        }

        foreach (var role in new[] { AccountRoles.Viewer, AccountRoles.Customer })
        {
            using var client = await fixture.CreateAccountClientAsync(role, [(DeviceId, "operate")]);
            using var response = await client.Client.GetAsync(Endpoint);
            Check(response.StatusCode == HttpStatusCode.NotFound,
                $"{role} with an operate grant cannot use the staff-only memory status route");
            VerifySecurityHeaders(response);
        }

        foreach (var role in new[] { AccountRoles.Owner, AccountRoles.Administrator })
        {
            using var client = await fixture.CreateAccountClientAsync(role);
            using var response = await client.Client.GetAsync(Endpoint);
            Check(response.StatusCode == HttpStatusCode.OK,
                $"{role} has broad access to configured device memory status");
            VerifySecurityHeaders(response);

            using var unconfigured = await client.Client.GetAsync(
                "/app/v1/tonight/devices/unconfigured-device/memory-status");
            Check(unconfigured.StatusCode == HttpStatusCode.NotFound,
                $"{role} cannot use broad account rights to expose an unconfigured device");
            VerifySecurityHeaders(unconfigured);
        }

        await VerifyCurrentGrantRevocationAsync(fixture);
        await VerifyCurrentRoleRevocationAsync(fixture);
        await VerifyExpiredSessionAsync(fixture);
        await VerifyDeletedSessionAsync(fixture);
        await VerifyDisabledUserAsync(fixture);
        await VerifySecurityStampRevocationAsync(fixture);
    }

    private static async Task VerifyCurrentGrantRevocationAsync(Fixture fixture)
    {
        using var account = await fixture.CreateAccountClientAsync(AccountRoles.Operator,
            [(DeviceId, "operate")]);
        using (var before = await account.Client.GetAsync(Endpoint))
        {
            Check(before.StatusCode == HttpStatusCode.OK, "operator is admitted before grant revocation");
        }

        await fixture.RemoveGrantAsync(account.UserId, DeviceId, "operate");
        using var after = await account.Client.GetAsync(Endpoint);
        Check(after.StatusCode == HttpStatusCode.NotFound,
            "grant revocation takes effect for an already-issued account cookie");
        VerifySecurityHeaders(after);
    }

    private static async Task VerifyCurrentRoleRevocationAsync(Fixture fixture)
    {
        using var account = await fixture.CreateAccountClientAsync(AccountRoles.Operator,
            [(DeviceId, "operate")]);
        using (var before = await account.Client.GetAsync(Endpoint))
        {
            Check(before.StatusCode == HttpStatusCode.OK, "operator role admits before role revocation");
        }

        await fixture.RemoveRoleAsync(account.UserId, AccountRoles.Operator);
        using var after = await account.Client.GetAsync(Endpoint);
        Check(after.StatusCode == HttpStatusCode.NotFound,
            "current database roles replace stale role claims on an existing cookie");
        VerifySecurityHeaders(after);
    }

    private static async Task VerifyExpiredSessionAsync(Fixture fixture)
    {
        using var account = await fixture.CreateAccountClientAsync(AccountRoles.Owner);
        await fixture.ExpireSessionAsync(account.SessionId);
        using var response = await account.Client.GetAsync(Endpoint);
        Check(response.StatusCode == HttpStatusCode.Unauthorized,
            "expired account session invalidates its still-protected cookie");
    }

    private static async Task VerifyDisabledUserAsync(Fixture fixture)
    {
        using var account = await fixture.CreateAccountClientAsync(AccountRoles.Owner);
        await fixture.DisableUserAsync(account.UserId);
        using var response = await account.Client.GetAsync(Endpoint);
        Check(response.StatusCode == HttpStatusCode.Unauthorized,
            "disabled account cannot use a previously issued cookie");
    }

    private static async Task VerifyDeletedSessionAsync(Fixture fixture)
    {
        using var account = await fixture.CreateAccountClientAsync(AccountRoles.Owner);
        await fixture.DeleteSessionAsync(account.SessionId);
        using var response = await account.Client.GetAsync(Endpoint);
        Check(response.StatusCode == HttpStatusCode.Unauthorized,
            "deleting an account session invalidates its still-protected cookie");
    }

    private static async Task VerifySecurityStampRevocationAsync(Fixture fixture)
    {
        using var account = await fixture.CreateAccountClientAsync(AccountRoles.Owner);
        await fixture.RotateSecurityStampAsync(account.UserId);
        using var response = await account.Client.GetAsync(Endpoint);
        Check(response.StatusCode == HttpStatusCode.Unauthorized,
            "security stamp change invalidates a previously issued cookie");
    }

    private static async Task VerifyDisabledAccountMappingAsync()
    {
        await using var fixture = await Fixture.StartAsync(identityEnabled: false);
        using var client = fixture.Client();
        using var response = await client.GetAsync(Endpoint);
        Check(response.StatusCode == HttpStatusCode.ServiceUnavailable,
            "identity-disabled configuration does not map the account route");
        VerifySecurityHeaders(response);
    }

    private static async Task VerifyFullResponseAsync(HttpResponseMessage response)
    {
        Check(response.StatusCode == HttpStatusCode.OK, "authorized request returns memory status");
        VerifySecurityHeaders(response);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "storedBytes", "capacityBytes", "storageStatus", "rejectedWrites",
            "storageIncomplete", "lastStorageErrorCode", "lastStorageErrorAt"
        };
        Check(root.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal).SetEquals(expected),
            "response contains exactly the seven allowlisted diagnostic fields");
        Check(root.GetProperty("storedBytes").GetInt64() == 2_000
            && root.GetProperty("capacityBytes").GetInt64() == 2_000
            && root.GetProperty("storageStatus").GetString() == "full"
            && root.GetProperty("rejectedWrites").GetInt64() == 1
            && root.GetProperty("storageIncomplete").GetBoolean()
            && root.GetProperty("lastStorageErrorCode").GetString() == "archive_capacity",
            "full archive returns expected shared storage counters and known error code");
        Check(root.GetProperty("lastStorageErrorAt").TryGetDateTimeOffset(out var errorAt)
            && errorAt.Offset == TimeSpan.Zero,
            "known archive capacity error includes a UTC timestamp");
    }

    private static void VerifySecurityHeaders(HttpResponseMessage response)
    {
        Check(response.Headers.CacheControl?.NoStore == true,
            "account endpoint response disables caching");
        Check(response.Headers.TryGetValues("X-Content-Type-Options", out var values)
            && values.Contains("nosniff", StringComparer.OrdinalIgnoreCase),
            "account endpoint response sets nosniff");
    }

    private static void Check(bool condition, string detail)
    {
        if (!condition)
        {
            throw new Exception("Tonight memory status: " + detail);
        }
    }

    private sealed record AccountClient(HttpClient Client, string UserId, string SessionId) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    private sealed class Fixture(WebApplication app, string root, X509Certificate2 certificate) : IAsyncDisposable
    {
        private const string Scheme = "__Host-dv-account";
        private readonly Uri baseAddress = GetAddress(app);
        internal WebApplication App { get; } = app;
        internal EveningMemoryStore MemoryStore { get; } = app.Services.GetRequiredService<EveningMemoryStore>();
        internal OperationJournal Journal { get; } = app.Services.GetRequiredService<OperationJournal>();
        internal OpenAiVectorMemoryProvider Provider { get; } = app.Services.GetRequiredService<OpenAiVectorMemoryProvider>();
        internal TimeProvider Clock { get; } = app.Services.GetRequiredService<TimeProvider>();
        internal string Root { get; } = root;

        internal static async Task<Fixture> StartAsync(bool identityEnabled = true)
        {
            var root = Path.Combine(Path.GetTempPath(), "dv-tonight-memory-status-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                "CN=localhost", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(Configuration(root, identityEnabled));
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
                listen => listen.UseHttps(certificate)));
            builder.Services.AddAccountIdentity(builder.Configuration);

            var clock = TimeProvider.System;
            var journal = new OperationJournal(builder.Configuration, clock);
            var options = HybridMemoryOptions.Read(builder.Configuration);
            var memoryStore = new EveningMemoryStore(builder.Configuration, journal, clock);
            var provider = new OpenAiVectorMemoryProvider(new HttpClient(new NoNetworkHandler()), null, options, ownsHttp: true);
            var memory = new HybridMemoryService(memoryStore, provider, options, clock);
            builder.Services.AddSingleton(clock);
            builder.Services.AddSingleton(journal);
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(memoryStore);
            builder.Services.AddSingleton(provider);
            builder.Services.AddSingleton(memory);

            var app = builder.Build();
            if (identityEnabled)
            {
                await AccountProvisioning.MigrateAsync(app.Services);
                await app.InitializeAccountsAsync();
            }
            app.UseRateLimiter();
            app.UseAccountIdentity();
            app.MapAccountEndpoints();
            app.MapTonightEndpoints();
            await app.StartAsync();
            return new Fixture(app, root, certificate);
        }

        internal HttpClient Client()
        {
            var cookies = new CookieContainer();
            var handler = new HttpClientHandler
            {
                CookieContainer = cookies,
                UseProxy = false,
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            var client = new HttpClient(handler) { BaseAddress = baseAddress };
            client.DefaultRequestHeaders.Add("Origin", baseAddress.GetLeftPart(UriPartial.Authority));
            return client;
        }

        internal async Task SeedFullArchiveAsync()
        {
            var access = new MemoryAccess("tonight-memory-test", MemoryStore.GetStatus("tonight-memory-test").EveningId,
                Guid.NewGuid().ToString("D"), PersonalityCatalog.OllivanderId);
            var record = MemoryStore.WriteMemory(access, new(Guid.NewGuid().ToString("D"), MemoryScope.Conversation,
                MemoryKind.UserReportedFact, new string('x', 2_000), "tonight-capacity-fixture"));
            Check(record is not null, "capacity fixture record reaches the exact storage boundary");
            Check(MemoryStore.WriteMemory(access, new(Guid.NewGuid().ToString("D"), MemoryScope.Conversation,
                MemoryKind.UserReportedFact, "y", "tonight-capacity-rejection")) is null,
                "one over-capacity fixture write persists a rejection");
            await Task.CompletedTask;
        }

        internal HttpClient CreateCookieClient(string protectedTicket)
        {
            var cookies = new CookieContainer();
            cookies.Add(baseAddress, new Cookie(Scheme, protectedTicket, "/")
            {
                Secure = true,
                HttpOnly = true
            });
            var handler = new HttpClientHandler
            {
                CookieContainer = cookies,
                UseProxy = false,
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            var client = new HttpClient(handler) { BaseAddress = baseAddress };
            client.DefaultRequestHeaders.Add("Origin", baseAddress.GetLeftPart(UriPartial.Authority));
            return client;
        }

        internal async Task<AccountClient> CreateAccountClientAsync(string role,
            IReadOnlyList<(string DeviceId, string Permission)>? grants = null)
        {
            var userId = "user-" + Guid.NewGuid().ToString("N");
            var sessionId = Guid.NewGuid().ToString("N");
            string ticket;
            await using (var scope = App.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var users = services.GetRequiredService<UserManager<AccountUser>>();
                var user = new AccountUser { Id = userId, UserName = userId, DisplayName = userId };
                Check((await users.CreateAsync(user)).Succeeded, "fixture account is created by Identity");
                Check((await users.AddToRoleAsync(user, role)).Succeeded, "fixture account receives its database role");

                var database = services.GetRequiredService<AccountDatabase>();
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                database.Sessions.Add(new AccountSession
                {
                    Id = sessionId,
                    UserId = userId,
                    AuthenticatedAt = now,
                    ExpiresAt = now + 3_600,
                    LastSeenAt = now
                });
                if (grants is not null)
                {
                    foreach (var (deviceId, permission) in grants)
                    {
                        database.ResourceGrants.Add(new AccountResourceGrant
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            UserId = userId,
                            ResourceType = "device",
                            ResourceId = deviceId,
                            Permission = permission
                        });
                    }
                }
                await database.SaveChangesAsync();

                var principal = await services.GetRequiredService<IUserClaimsPrincipalFactory<AccountUser>>().CreateAsync(user);
                ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(AccountIdentity.SessionClaim, sessionId));
                var cookieOptions = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                    .Get(IdentityConstants.ApplicationScheme);
                ticket = cookieOptions.TicketDataFormat.Protect(new AuthenticationTicket(principal,
                    new AuthenticationProperties
                    {
                        IssuedUtc = DateTimeOffset.UtcNow,
                        ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1)
                    }, IdentityConstants.ApplicationScheme));
            }
            return new AccountClient(CreateCookieClient(ticket), userId, sessionId);
        }

        internal async Task RemoveGrantAsync(string userId, string deviceId, string permission)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            await database.ResourceGrants.Where(grant => grant.UserId == userId
                    && grant.ResourceId == deviceId && grant.Permission == permission)
                .ExecuteDeleteAsync();
        }

        internal async Task RemoveRoleAsync(string userId, string role)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            var roleId = await database.Roles.Where(candidate => candidate.Name == role)
                .Select(candidate => candidate.Id).SingleAsync();
            await database.UserRoles.Where(membership => membership.UserId == userId && membership.RoleId == roleId)
                .ExecuteDeleteAsync();
        }

        internal async Task ExpireSessionAsync(string sessionId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            await database.Sessions.Where(session => session.Id == sessionId)
                .ExecuteUpdateAsync(update => update.SetProperty(session => session.ExpiresAt,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1));
        }

        internal async Task DeleteSessionAsync(string sessionId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            await database.Sessions.Where(session => session.Id == sessionId).ExecuteDeleteAsync();
        }

        internal async Task DisableUserAsync(string userId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            await database.Users.Where(user => user.Id == userId)
                .ExecuteUpdateAsync(update => update.SetProperty(user => user.Disabled, true));
        }

        internal async Task RotateSecurityStampAsync(string userId)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            await database.Users.Where(user => user.Id == userId)
                .ExecuteUpdateAsync(update => update.SetProperty(user => user.SecurityStamp, Guid.NewGuid().ToString("N")));
        }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
            Provider.Dispose();
            Journal.Dispose();
            certificate.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static Uri GetAddress(WebApplication app)
        {
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            return new Uri(address.Replace("127.0.0.1", "localhost", StringComparison.Ordinal));
        }

        private static Dictionary<string, string?> Configuration(string root, bool identityEnabled) => new()
        {
            ["Backend:DatabasePath"] = Path.Combine(root, "journal.sqlite"),
            ["Backend:Memory:Enabled"] = "true",
            ["Backend:Memory:AppNamespace"] = "tonight-tests",
            ["Backend:Memory:Environment"] = "Testing",
            ["Backend:Memory:MaxStoredBytes"] = "2000",
            ["ApplicationIdentity:Enabled"] = identityEnabled.ToString(),
            ["ApplicationIdentity:DataRoot"] = Path.Combine(root, "accounts"),
            ["ApplicationIdentity:DataProtectionRoot"] = Path.Combine(root, "keys"),
            ["ApplicationIdentity:RelyingPartyId"] = "localhost",
            ["ApplicationIdentity:AllowedOrigins:0"] = "https://localhost",
            ["ApplicationIdentity:RequestsPerMinute"] = "120",
            ["Devices:0:DeviceId"] = DeviceId,
            ["Devices:0:CredentialSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(LegacyDeviceSecret))),
            ["Devices:1:DeviceId"] = OtherDeviceId
        };
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Tonight memory status must not make provider requests.");
    }
}
