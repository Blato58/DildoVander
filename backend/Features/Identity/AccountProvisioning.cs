using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;

namespace Kufr.Backend.Features.Identity;

internal static class AccountProvisioning
{
    public static bool IsCommand(string[] args) => args.Any(x => x is "--account-migrate" or "--account-bootstrap" or "--account-recovery");

    public static async Task<int> RunAsync(WebApplication app, string[] args)
    {
        try
        {
            var config = app.Services.GetRequiredService<AccountConfiguration>();
            if (!config.Enabled) { throw new InvalidOperationException("ApplicationIdentity must be explicitly configured before account provisioning."); }
            if (args is ["--account-migrate"])
            {
                var backup = await MigrateAsync(app.Services);
                await app.InitializeAccountsAsync();
                Console.WriteLine(backup is null ? "Account schema is current; no existing database required a backup."
                    : $"Account schema upgraded. Private consistent backup: {backup}");
                return 0;
            }
            await app.InitializeAccountsAsync();
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AccountUser>>();
            AccountInvitation invitation;
            var token = AccountEndpoints.Secret();
            await using var transaction = await db.Database.BeginTransactionAsync();
            if (args is ["--account-bootstrap"])
            {
                if (await db.Users.AnyAsync()) { throw new InvalidOperationException("Bootstrap is closed because accounts already exist. Use scoped account recovery."); }
                await db.Invitations.Where(x => x.Purpose == "bootstrap-local" && !x.Consumed)
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.Consumed, true));
                invitation = new AccountInvitation { TokenHash = AccountEndpoints.Hash(token), Purpose = "bootstrap-local", Role = AccountRoles.Owner };
            }
            else if (args is ["--account-recovery", var userId])
            {
                var user = await users.FindByIdAsync(userId);
                if (user is not { Disabled: false }) { throw new InvalidOperationException("Recovery target must be an existing active account ID."); }
                invitation = new AccountInvitation { TokenHash = AccountEndpoints.Hash(token), Purpose = "recovery", UserId = user.Id };
            }
            else { throw new InvalidOperationException("Use --account-migrate, --account-bootstrap, or --account-recovery <account-id>."); }
            invitation.ExpiresAt = AccountEndpoints.Now() + 1800;
            db.Invitations.Add(invitation);
            await db.SaveChangesAsync();
            var directory = Path.Combine(config.DataRoot, "owner-delivery");
            CreatePrivateDirectory(directory);
            var path = Path.Combine(directory, $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");
            var kind = invitation.Purpose == "recovery" ? "recovery" : "invite";
            await File.WriteAllTextAsync(path, $"{config.AllowedOrigins[0]}/signin#{kind}={token}\nExpires UTC: {DateTimeOffset.FromUnixTimeSeconds(invitation.ExpiresAt):O}\nSingle use. Deliver privately and remove this file after enrollment.\n");
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            await transaction.CommitAsync();
            Console.WriteLine($"One-time account link written to private file: {path}. Expires in 30 minutes. The link is never printed.");
            return 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or SqliteException)
        {
            Console.Error.WriteLine($"Account provisioning failed: {exception.Message}");
            return 1;
        }
    }

    internal static async Task<string?> MigrateAsync(IServiceProvider services)
    {
        var config = services.GetRequiredService<AccountConfiguration>();
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
        var path = Path.Combine(config.DataRoot, "identity.sqlite");
        var existed = File.Exists(path);
        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        if ((await db.Database.GetAppliedMigrationsAsync()).Any(x => !known.Contains(x)))
        {
            throw new InvalidOperationException("Account schema is newer than this binary. Restore a compatible application; automatic downgrade is forbidden.");
        }
        if (!(await db.Database.GetPendingMigrationsAsync()).Any()) { return null; }
        string? backup = null;
        if (existed)
        {
            var directory = Path.Combine(config.DataRoot, "backups");
            CreatePrivateDirectory(directory);
            backup = Path.Combine(directory, $"identity-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.sqlite");
            using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
            using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup }.ToString());
            await source.OpenAsync(); await destination.OpenAsync();
            source.BackupDatabase(destination);
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(backup, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        }
        await db.Database.MigrateAsync();
        return backup;
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(path); }
        else { Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }
}
