using Kufr.Backend.Features.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;

internal static class AccountMigrationTests
{
    public static async Task RunAsync(WebApplication sourceApp)
    {
        var root = Path.Combine(Path.GetTempPath(), "dv-account-upgrade-" + Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApplicationIdentity:Enabled"] = "true", ["ApplicationIdentity:DataRoot"] = root,
            ["ApplicationIdentity:DataProtectionRoot"] = Path.Combine(root, "keys"),
            ["ApplicationIdentity:RelyingPartyId"] = "localhost", ["ApplicationIdentity:AllowedOrigins:0"] = "https://localhost"
        }).Build();
        var services = new ServiceCollection(); services.AddLogging(); services.AddAccountIdentity(configuration);
        await using var provider = services.BuildServiceProvider();
        try
        {
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
                var prior = db.Database.GetMigrations().Single(x => x.EndsWith("_AccountPasskeyStorage", StringComparison.Ordinal));
                await db.GetService<IMigrator>().MigrateAsync(prior);
                await db.Database.OpenConnectionAsync();
                var source = Path.Combine(sourceApp.Services.GetRequiredService<AccountConfiguration>().DataRoot, "identity.sqlite");
                await db.Database.ExecuteSqlRawAsync("ATTACH DATABASE {0} AS evidence", source);
                // Seed the actual previous schema with verifier-created evidence.
                // Its user creation audit column intentionally does not exist yet.
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO AspNetUsers (Id,UserName,NormalizedUserName,Email,NormalizedEmail,EmailConfirmed,
                        PasswordHash,SecurityStamp,ConcurrencyStamp,PhoneNumber,PhoneNumberConfirmed,TwoFactorEnabled,
                        LockoutEnd,LockoutEnabled,AccessFailedCount,DisplayName,Disabled)
                    SELECT Id,UserName,NormalizedUserName,Email,NormalizedEmail,EmailConfirmed,
                        PasswordHash,SecurityStamp,ConcurrencyStamp,PhoneNumber,PhoneNumberConfirmed,TwoFactorEnabled,
                        LockoutEnd,LockoutEnabled,AccessFailedCount,DisplayName,Disabled FROM evidence.AspNetUsers;
                    INSERT INTO AspNetRoles SELECT * FROM evidence.AspNetRoles;
                    INSERT INTO AspNetUserRoles SELECT * FROM evidence.AspNetUserRoles;
                    INSERT INTO AspNetUserPasskeys SELECT * FROM evidence.AspNetUserPasskeys;
                    INSERT INTO Sessions SELECT * FROM evidence.Sessions;
                    INSERT INTO ResourceGrants SELECT * FROM evidence.ResourceGrants;
                    DETACH DATABASE evidence;
                    """);
                await db.Database.CloseConnectionAsync();
            }
            var backup = await AccountProvisioning.MigrateAsync(provider);
            Check(backup is not null && File.Exists(backup), "upgrade preserves a private consistent SQLite backup");
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
                Check(await db.Users.CountAsync() == 2 && await db.UserPasskeys.CountAsync() == 2, "upgrade retains real users and passkeys");
                Check(await db.Sessions.CountAsync() >= 2 && await db.ResourceGrants.CountAsync() == 2, "upgrade retains sessions and grants");
                Check(!await db.Users.AnyAsync(x => x.CreatedAt != 0), "legacy account creation time stays unknown");
                await db.Database.ExecuteSqlRawAsync("INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('99999999999999_UnknownFuture', '10.0.12')");
            }
            var rejected = false;
            try { await AccountProvisioning.MigrateAsync(provider); }
            catch (InvalidOperationException error) when (error.Message.Contains("newer than this binary", StringComparison.Ordinal)) { rejected = true; }
            Check(rejected, "unknown newer schema rejected");
            using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup }.ToString());
            using var restored = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "recovery-check.sqlite") }.ToString());
            await snapshot.OpenAsync(); await restored.OpenAsync(); snapshot.BackupDatabase(restored);
            foreach (var table in new[] { "AspNetUserPasskeys", "Sessions", "ResourceGrants" })
            {
                using var command = restored.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM " + table;
                Check(Convert.ToInt32(await command.ExecuteScalarAsync()) >= 2, "backup recovery retains " + table);
            }
            Console.WriteLine("PASS account versioned SQLite upgrade, real passkey/session/grant preservation, private backup recovery and newer-schema rejection");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
    private static void Check(bool condition, string label) { if (!condition) { throw new InvalidOperationException("Account migration: " + label); } }
}
