using System.Text.Json;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;

internal static partial class AccountIdentityTests
{
    internal static async Task RunCustomerDataBrowserFixtureAsync()
    {
        await using (var fixture = await Fixture.StartAsync(browserMode: true,
            customerMemoryFeatures: true, accountDataExportFeatures: true))
        {
            // Customer self-enrollment intentionally requires an existing Owner.
            // Provision only this fixture prerequisite directly, following the
            // identity tests' real UserManager/role setup; browser enrollment
            // below still uses the production passkey endpoints.
            using (var scope = fixture.App.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<AccountUser>>();
                var ownerId = Guid.NewGuid().ToString("N");
                var owner = new AccountUser { Id = ownerId, UserName = ownerId, DisplayName = "Customer-data fixture owner" };
                Check((await users.CreateAsync(owner)).Succeeded, "fixture owner identity is created");
                Check((await users.AddToRoleAsync(owner, AccountRoles.Owner)).Succeeded, "fixture owner receives Owner role");
            }

            var seededAccounts = new HashSet<string>(StringComparer.Ordinal);
            using var stopping = new CancellationTokenSource();
            Console.CancelKeyPress += OnCancel;
            Console.WriteLine("CUSTOMER_DATA_BROWSER_FIXTURE "
                + fixture.App.Urls.Single().Replace("127.0.0.1", "localhost", StringComparison.Ordinal));

            async Task ReadCommandsAsync()
            {
                while (await Console.In.ReadLineAsync() is { } line)
                {
                    if (line == "stop")
                    {
                        stopping.Cancel();
                        return;
                    }

                    const string seedPrefix = "seed-memory ";
                    if (!line.StartsWith(seedPrefix, StringComparison.Ordinal) || line.Length > 4096)
                    {
                        Console.WriteLine("CUSTOMER_DATA_FIXTURE_COMMAND_REJECTED");
                        continue;
                    }

                    try
                    {
                        var (accountId, profileId, revision) = ParseSeedCommand(line[seedPrefix.Length..]);
                        if (!seededAccounts.Add(accountId))
                        {
                            throw new InvalidOperationException("Memory seed already used for this fixture account.");
                        }

                        await SeedCustomerMemoryAsync(fixture.App, accountId, profileId, revision);
                        Console.WriteLine("CUSTOMER_DATA_MEMORY_SEEDED");
                    }
                    catch
                    {
                        Console.WriteLine("CUSTOMER_DATA_MEMORY_SEED_FAILED");
                    }
                }
            }

            void OnCancel(object? sender, ConsoleCancelEventArgs args)
            {
                args.Cancel = true;
                stopping.Cancel();
            }

            var commandTask = Task.Run(ReadCommandsAsync);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stopping.Token);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
            }
            finally
            {
                Console.CancelKeyPress -= OnCancel;
            }

            await commandTask;
        }

        Console.WriteLine("CUSTOMER_DATA_FIXTURE_STOPPED");
    }

    private static (string AccountId, string ProfileId, long Revision) ParseSeedCommand(string encoded)
    {
        if (encoded.Length is < 8 or > 2048)
        {
            throw new InvalidOperationException("Invalid fixture seed command.");
        }

        using var document = JsonDocument.Parse(Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(encoded));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Count() != 3
            || !root.TryGetProperty("accountId", out var accountElement)
            || accountElement.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("profileId", out var profileElement)
            || profileElement.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("revision", out var revisionElement)
            || !revisionElement.TryGetInt64(out var revision))
        {
            throw new InvalidOperationException("Invalid fixture seed command.");
        }

        var accountId = accountElement.GetString()!;
        var profileId = profileElement.GetString()!;
        if ((!Guid.TryParseExact(accountId, "D", out _) && !Guid.TryParseExact(accountId, "N", out _))
            || (!Guid.TryParseExact(profileId, "D", out _) && !Guid.TryParseExact(profileId, "N", out _))
            || revision < 1)
        {
            throw new InvalidOperationException("Invalid fixture seed command.");
        }

        return (accountId, profileId, revision);
    }

    private static async Task SeedCustomerMemoryAsync(WebApplication app,
        string accountId, string profileId, long revision)
    {
        using (var scope = app.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            var user = await database.Users.SingleOrDefaultAsync(candidate => candidate.Id == accountId);
            if (user is null || user.Disabled
                || !await scope.ServiceProvider.GetRequiredService<UserManager<AccountUser>>()
                    .IsInRoleAsync(user, AccountRoles.Customer))
            {
                throw new InvalidOperationException("Fixture seed requires an active customer account.");
            }
        }

        var profiles = app.Services.GetRequiredService<CharacterProfileStore>();
        var currentProfile = await profiles.CaptureAsync(accountId, profileId, revision);
        if (currentProfile is null)
        {
            throw new InvalidOperationException("Fixture seed requires the account's current saved profile.");
        }

        using var profileDocument = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            profileId = currentProfile.ProfileId,
            revision = currentProfile.Revision,
        }));
        using var memory = await CustomerMemoryHarness.CreateAsync(app, accountId, profileDocument.RootElement);
        var capturedProfile = await profiles.CaptureAsync(accountId, profileId, revision)
            ?? throw new InvalidOperationException("Fixture session requires the current character snapshot.");
        await StoreCustomerSessionSnapshotAsync(app, accountId, memory.CustomerSession.Id,
            new CustomerConversationSnapshot(capturedProfile, PersonalMemoryConsent: true, OpenAiConsent: true));
        SeedAllowedRecord(memory, "Moje oblíbená barva je modrá.", "browser-fixture");
    }

    private static async Task StoreCustomerSessionSnapshotAsync(WebApplication app, string accountId,
        string sessionId, CustomerConversationSnapshot snapshot)
    {
        var databasePath = app.Configuration["Backend:DatabasePath"]
            ?? throw new InvalidOperationException("Fixture journal database path is unavailable.");
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE customer_sessions SET snapshot=$snapshot WHERE id=$session AND owner_id=$account";
        command.Parameters.AddWithValue("$snapshot", json);
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$account", accountId);
        if (await command.ExecuteNonQueryAsync() != 1)
        {
            throw new InvalidOperationException("Fixture customer session snapshot was not updated.");
        }
    }
}
