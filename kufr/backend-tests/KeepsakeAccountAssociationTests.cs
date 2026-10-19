using Kufr.Backend.Features.Keepsakes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

internal static class KeepsakeAccountAssociationTests
{
    internal static async Task RunAsync(string directory)
    {
        var database = Path.Combine(directory, "account-association.db");
        using (var legacy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString()))
        {
            legacy.Open();
            using var command = legacy.CreateCommand();
            command.CommandText = """
                CREATE TABLE dv10_keepsakes (
                    id TEXT PRIMARY KEY, outcome_key TEXT NOT NULL UNIQUE, device_id TEXT NOT NULL,
                    epoch INTEGER NOT NULL DEFAULT 0, photo_epoch INTEGER NOT NULL DEFAULT 0,
                    deleted INTEGER NOT NULL DEFAULT 0, claimed INTEGER NOT NULL DEFAULT 0, evening_id TEXT,
                    published_at_ticks INTEGER, published_edition_id TEXT, document TEXT);
                INSERT INTO dv10_keepsakes(id,outcome_key,device_id,deleted,document)
                    VALUES('legacy-record','legacy-outcome','legacy-device',1,'historical fixture record');
                """;
            command.ExecuteNonQuery();
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = database,
            ["Keepsake:DataRoot"] = Path.Combine(directory, "account-association-content"),
            ["Keepsake:Enabled"] = "true", ["Keepsake:GenerationEnabled"] = "true", ["Keepsake:PortraitEnabled"] = "true"
        }).Build();
        var clock = new AssociationClock();
        var store = new KeepsakeStore(config, clock);
        using (var migrated = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString()))
        {
            migrated.Open();
            using var command = migrated.CreateCommand();
            command.CommandText = "SELECT account_access_epoch,document FROM dv10_keepsakes WHERE id='legacy-record'";
            using var reader = command.ExecuteReader();
            Check(reader.Read() && reader.GetInt32(0) == 0 && reader.GetString(1) == "historical fixture record", "additive SQLite migration backfills epoch zero without rewriting retained records");
        }
        var provider = new FakeKeepsakeProvider();
        var first = store.Issue(Snapshot());
        var owner = store.Bootstrap();
        store.Claim(store.IssueClaim(first.Id, "device-association").Token, owner.Token, "claim-owner");
        await CompleteNextAsync();
        var edition = store.Read(first.Id, owner.Token).Editions.Single();
        var proof = store.AuthorizeAccountAssociation(first.Id, owner.Token);
        Check(proof.Locator == first.Id && store.IsAccountAssociationCurrent(proof.Locator, proof.Epoch), "owner authority binds the exact locator and access epoch");
        Check(store.RecallAccountAssociationTicket(proof, owner.Token, "account-a", "association-key") is null, "fresh association has no replay");
        Check(store.RememberAccountAssociationTicket(proof, owner.Token, "account-a", "association-key", "synthetic-ticket-a") == "synthetic-ticket-a"
            && store.RememberAccountAssociationTicket(proof, owner.Token, "account-a", "association-key", "concurrent-unused-ticket") == "synthetic-ticket-a"
            && store.RecallAccountAssociationTicket(proof, owner.Token, "account-a", "association-key") == "synthetic-ticket-a", "lost and concurrent issue responses return the same protected ticket");
        try
        {
            store.RecallAccountAssociationTicket(proof, owner.Token, "account-b", "association-key");
            throw new InvalidOperationException("Association idempotency key must not change its target account.");
        }
        catch (KeepsakeException exception) when (exception.Code == "idempotency_conflict") { }

        var share = store.Bootstrap();
        store.ShareClaim(store.CreateShare(first.Id, owner.Token, edition.Id, "issue-share").Token, share.Token, "claim-share");
        ExpectDenied(() => store.AuthorizeAccountAssociation(first.Id, share.Token));
        var stranger = store.Bootstrap();
        ExpectDenied(() => store.AuthorizeAccountAssociation(first.Id, stranger.Token));
        var second = store.Issue(Snapshot());
        ExpectDenied(() => store.AuthorizeAccountAssociation(second.Id, owner.Token));
        await CompleteNextAsync();

        var consent = store.SetPortraitConsent(first.Id, owner.Token, true, "dv10-portrait-v1", "portrait-consent");
        var syntheticPng = store.ReadAsset(first.Id, owner.Token, edition.ArtworkAssetId).Bytes;
        using var input = new MemoryStream(syntheticPng);
        var upload = await store.AcceptPhotoAsync(first.Id, owner.Token, input, "image/png", "portrait-upload", consent);
        store.EnqueuePortrait(first.Id, owner.Token, upload.Id, edition.Id, "portrait_only", true, "portrait-job");
        await CompleteNextAsync();
        var accountView = store.ReadForAccount(first.Id, proof.Epoch);
        var portrait = accountView.Portraits.Single();
        Check(accountView.ReadOnly && accountView.Uploads.Count == 0 && accountView.Jobs.Count == 0, "account projection is read-only and excludes source uploads and generation internals");
        Check(accountView.Assets.Any(asset => asset.Id == portrait.AssetId), "private portrait results are included only in associated account projection");
        Check(store.ReadAssetForAccount(first.Id, proof.Epoch, portrait.AssetId).Bytes.Length > 0, "exact account proof can read its result portrait bytes");
        ExpectDenied(() => store.ReadAssetForAccount(first.Id, proof.Epoch, upload.OriginalAssetId));
        ExpectDenied(() => store.ReadAssetForAccount(first.Id, proof.Epoch, upload.NormalizedAssetId));
        ExpectDenied(() => store.ReadAssetForAccount(second.Id, proof.Epoch, portrait.AssetId));
        Check(store.Read(first.Id, share.Token, true, edition.Id).Portraits.Count == 0, "public share remains base-only after explicit account association");

        var recovery = store.CreateRecovery(first.Id, owner.Token, "return-owner");
        clock.Utc = clock.Utc.AddDays(31);
        Check(store.IsAccountAssociationCurrent(first.Id, proof.Epoch), "account access is independent of guest cookie expiry");
        Check(store.ReadForAccount(first.Id, proof.Epoch).Portraits.Count == 1, "expired guest browser does not delete or expire account association");
        ExpectDenied(() => store.AuthorizeAccountAssociation(first.Id, owner.Token));
        var returning = store.Bootstrap();
        store.Recover(recovery.Token, returning.Token, "recover-owner");
        store.RevokeAllAccess(first.Id, returning.Token);
        Check(!store.IsAccountAssociationCurrent(first.Id, proof.Epoch), "revoke-all invalidates prior account proof");
        ExpectDenied(() => store.RecallAccountAssociationTicket(proof, returning.Token, "account-a", "association-key"));
        ExpectDenied(() => store.ReadForAccount(first.Id, proof.Epoch));
        ExpectDenied(() => store.ReadAssetForAccount(first.Id, proof.Epoch, portrait.AssetId));

        var third = store.Issue(Snapshot());
        var thirdOwner = store.Bootstrap();
        store.Claim(store.IssueClaim(third.Id, "device-association").Token, thirdOwner.Token, "claim-third");
        var pending = store.LeaseNext()!;
        var request = store.Dispatch(pending)!;
        store.RevokeAllAccess(third.Id, thirdOwner.Token);
        Check(store.Complete(pending, await provider.GenerateAsync(request, default)), "access revocation preserves the existing generation epoch and job behavior");

        async Task CompleteNextAsync()
        {
            var job = store.LeaseNext() ?? throw new InvalidOperationException("Missing synthetic fixture job.");
            Check(store.Complete(job, await provider.GenerateAsync(store.Dispatch(job)!, default)), "synthetic fixture job completed");
        }
        Console.WriteLine("Keepsake account proof tests passed: owner/share/locator isolation, private-result projection, raw-photo denial, expiry and revocation fences.");
    }

    private static KeepsakeIssueSnapshot Snapshot() => new(Guid.NewGuid().ToString("D"), "device-association", 1,
        "fixture-wand", "Synthetic fixture wand", "Synthetic catalog facts", "Physical state is not verified by this test.");
    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
    private static void ExpectDenied(Action action)
    {
        try { action(); }
        catch (KeepsakeException exception) when (exception.Status is 401 or 403 or 404) { return; }
        throw new InvalidOperationException("Expected scoped archive access to be denied.");
    }
    private sealed class AssociationClock : TimeProvider
    {
        internal DateTimeOffset Utc { get; set; } = new(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Utc;
    }
}
