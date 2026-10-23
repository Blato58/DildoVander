using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Keepsakes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Identity;

internal static class AccountCollectionTests
{
    internal static async Task RunAsync()
    {
        await AccountUpgradeBackfillsExplicitLinkRevisionsAsync();
        await EnsureCreatedHasCollectionRevisionSchemaAsync();
        KeepsakeUpgradeBackfillsOwnerLinkRevisions();
    }

    private static async Task AccountUpgradeBackfillsExplicitLinkRevisionsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "dv-account-collection-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "identity.sqlite");
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<IdentityOptions>(options => options.Stores.SchemaVersion = IdentitySchemaVersions.Version3);
        services.AddDbContext<AccountDatabase>(options => options.UseSqlite("Data Source=" + path));
        await using var provider = services.BuildServiceProvider();
        try
        {
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
                var migrations = db.Database.GetMigrations().ToArray();
                var targetIndex = Array.FindIndex(migrations, id => id.EndsWith("_AccountCollectionPagination", StringComparison.Ordinal));
                Check(targetIndex > 0, "generated collection migration follows the existing identity schema");
                await db.GetService<IMigrator>().MigrateAsync(migrations[targetIndex - 1]);
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO ArchiveAssociations(UserId,Locator,Epoch,LinkedAt) VALUES
                        ('account-a','00000000-0000-0000-0000-000000000002',7,50),
                        ('account-a','00000000-0000-0000-0000-000000000001',4,50),
                        ('account-b','00000000-0000-0000-0000-000000000003',2,70);
                    """);
                await db.GetService<IMigrator>().MigrateAsync();
                var rows = await db.ArchiveAssociations.AsNoTracking().OrderBy(link => link.UserId)
                    .ThenBy(link => link.Locator).ToArrayAsync();
                Check(rows.Length == 3 && rows[0].CollectionRevision == 1
                    && rows[1].CollectionRevision == 2 && rows[2].CollectionRevision == 3,
                    "real EF upgrade backfills existing explicit links in deterministic source order");
                Check(await db.ArchiveCollectionStates.AsNoTracking().SingleAsync(state => state.Id == 1)
                    is { Revision: 3 }, "explicit link watermark is durable and contains no account identifier");
                await db.ArchiveAssociations.Where(link => link.UserId == "account-a").ExecuteDeleteAsync();
                Check(await db.ArchiveCollectionStates.AsNoTracking().SingleAsync(state => state.Id == 1)
                    is { Revision: 3 }, "removing links does not reset the source watermark");
                var nextRevision = AccountArchiveAssociations.AdvanceCollectionRevision(db);
                Check(nextRevision == 4, "a reclaimed link receives a revision above the source watermark");
                db.ArchiveAssociations.Add(new AccountArchiveAssociation
                {
                    UserId = "account-a", Locator = "00000000-0000-0000-0000-000000000004",
                    Epoch = 1, LinkedAt = 80, CollectionRevision = nextRevision.GetValueOrDefault()
                });
                await db.SaveChangesAsync();
            }
            SqliteConnection.ClearAllPools();
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
                var nextRevision = AccountArchiveAssociations.AdvanceCollectionRevision(db);
                Check(nextRevision == 5, "reopening the durable database does not reuse a deleted link revision");
                var state = await db.ArchiveCollectionStates.SingleAsync(value => value.Id == 1);
                state.Revision = long.MaxValue;
                await db.SaveChangesAsync();
                Check(AccountArchiveAssociations.AdvanceCollectionRevision(db) is null,
                    "the explicit association source fails closed rather than wrapping its maximum revision");
            }
            Console.WriteLine("PASS account collection EF upgrade preserves links and backfills monotonic source revisions");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static async Task EnsureCreatedHasCollectionRevisionSchemaAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "dv-account-collection-created-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<IdentityOptions>(options => options.Stores.SchemaVersion = IdentitySchemaVersions.Version3);
        services.AddDbContext<AccountDatabase>(options => options.UseSqlite("Data Source=" + Path.Combine(root, "identity.sqlite")));
        await using var provider = services.BuildServiceProvider();
        try
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            await db.Database.EnsureCreatedAsync();
            db.ArchiveAssociations.Add(new AccountArchiveAssociation
            {
                UserId = "fixture-account", Locator = "00000000-0000-0000-0000-000000000001", Epoch = 1, LinkedAt = 10
            });
            await db.SaveChangesAsync();
            Check(await db.ArchiveAssociations.Select(link => link.CollectionRevision).SingleAsync() == 0
                && await db.ArchiveCollectionStates.CountAsync() == 0,
                "EnsureCreated fixtures include the additive collection schema without requiring a migration row");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static void KeepsakeUpgradeBackfillsOwnerLinkRevisions()
    {
        var root = Path.Combine(Path.GetTempPath(), "dv-keepsake-collection-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dataRoot = Path.Combine(root, "keepsakes");
        var database = Path.Combine(root, "visitor.sqlite");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = database,
            ["Keepsake:DataRoot"] = dataRoot,
            ["Keepsake:DataProtectionRoot"] = Path.Combine(root, "keys")
        }).Build();
        try
        {
            _ = new KeepsakeStore(configuration, TimeProvider.System,
                DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys"))));
            using (var connection = new SqliteConnection("Data Source=" + database))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    ALTER TABLE dv10_customer_owners DROP COLUMN collection_revision;
                    DROP TABLE dv10_customer_collection_state;
                    INSERT INTO dv10_keepsakes(id,outcome_key,device_id,account_access_epoch,deleted,claimed,document)
                        VALUES('00000000-0000-0000-0000-000000000001','legacy-1','device',0,0,1,'{}'),
                              ('00000000-0000-0000-0000-000000000002','legacy-2','device',0,0,1,'{}');
                    INSERT INTO dv10_customer_owners(keepsake_id,owner_id,session_id,epoch,linked_at)
                        VALUES('00000000-0000-0000-0000-000000000001','account-a','session-a',0,100),
                              ('00000000-0000-0000-0000-000000000002','account-b','session-b',0,50);
                    """;
                command.ExecuteNonQuery();
            }

            var upgraded = new KeepsakeStore(configuration, TimeProvider.System,
                DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys"))));
            Check(upgraded.CustomerOwnedCollectionRevision("account-a") == 2,
                "legacy immutable owner links backfill a singleton keepsake watermark");
            var ownerA = upgraded.CustomerOwnedCollectionCandidates("account-a", 1, null, null, 10);
            Check(ownerA.Length == 0, "a global high-water excludes the later deterministic owner binding");
            ownerA = upgraded.CustomerOwnedCollectionCandidates("account-a", 2, null, null, 10);
            Check(ownerA.Length == 1 && ownerA[0].CollectionRevision == 2
                && ownerA[0].SessionId == "session-a", "owner rows retain their exact captured customer session and assigned revision");
            using (var connection = new SqliteConnection("Data Source=" + database))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "EXPLAIN QUERY PLAN SELECT keepsake_id FROM dv10_customer_owners WHERE owner_id='account-a' AND collection_revision<=2 ORDER BY linked_at DESC,keepsake_id COLLATE BINARY ASC LIMIT 201";
                using var reader = command.ExecuteReader();
                var plan = new List<string>();
                while (reader.Read()) { plan.Add(reader.GetString(3)); }
                Check(plan.Any(line => line.Contains("ix_dv10_customer_owners_collection_page", StringComparison.Ordinal)),
                    "owned collection keyset scans use the bounded owner/order index");
            }
            Console.WriteLine("PASS keepsake collection startup backfills immutable owner-link revisions without account-keyed counters");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) { throw new InvalidOperationException("Account collection: " + label); }
    }
}
