using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.CustomerData;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.Memory;
using Kufr.Backend.Features.PersonaLab;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

internal static partial class AccountIdentityTests
{
    public static async Task RunAccountDeletionAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "account-deletion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:DatabasePath"] = Path.Combine(root, "journal.sqlite"),
                ["Keepsake:DataRoot"] = Path.Combine(root, "archive"), ["Keepsake:Enabled"] = "true"
            }).Build();
            using var journal = new OperationJournal(config, TimeProvider.System);
            var memory = new EveningMemoryStore(config, journal);
            var characters = new CharacterProfileStore(root);
            var keepsakes = new KeepsakeStore(config, TimeProvider.System, memoryStore: memory);
            var lab = new PersonaLabStore(Path.Combine(root, "lab.sqlite"),
                LivePromptSnapshot.Create(PersonalityCatalog.OllivanderId, new GptLiveOptions()));
            var exports = new AccountDataExportStore(Path.Combine(root, "accounts"), TimeProvider.System);
            var conversations = new ConversationStore(TimeProvider.System, journal, eveningMemory: memory);
            var services = new ServiceCollection();
            services.Configure<IdentityOptions>(options => options.Stores.SchemaVersion = IdentitySchemaVersions.Version3);
            services.AddDbContext<AccountDatabase>(options => options.UseSqlite("Data Source=" + Path.Combine(root, "identity.sqlite")));
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton(journal); services.AddSingleton(memory); services.AddSingleton(characters);
            services.AddSingleton(keepsakes); services.AddSingleton(lab); services.AddSingleton(exports);
            services.AddSingleton(conversations);
            services.AddLogging();
            services.AddIdentityCore<AccountUser>(options => options.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
                .AddRoles<IdentityRole>().AddEntityFrameworkStores<AccountDatabase>();
            services.AddScoped<AccountDeletionService>();
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            await db.Database.MigrateAsync();
            db.Users.AddRange(new AccountUser { Id = "alice", UserName = "Alice", DisplayName = "private name" },
                new AccountUser { Id = "bob", UserName = "Bob" });
            db.Sessions.Add(new AccountSession { Id = "session", UserId = "alice" });
            db.ArchiveAssociations.Add(new AccountArchiveAssociation { UserId = "alice", Locator = "other-guest" });
            await db.SaveChangesAsync();
            var aliceExportAccess = new CustomerDataAccess("alice", "export-session-alice", "export-revision-alice");
            var aliceOldRequest = Guid.NewGuid();
            exports.Create(aliceExportAccess, aliceOldRequest);
            var aliceCurrentRequest = Guid.NewGuid();
            var aliceExport = exports.Create(aliceExportAccess, aliceCurrentRequest);
            var bobExportAccess = new CustomerDataAccess("bob", "export-session-bob", "export-revision-bob");
            var bobExportRequest = Guid.NewGuid();
            var bobExport = exports.Create(bobExportAccess, bobExportRequest);
            await characters.SaveAsync("alice", new(0, new CharacterUserInput("Secret", "Original")));
            await characters.SaveAsync("bob", new(0, new CharacterUserInput("Other", "Original")));
            var aliceLab = lab.CreateSession("alice", new(lab.ActiveRevisionId, "gpt-live-1", "first-visit", "empty", "private"));
            var bobLab = lab.CreateSession("bob", new(lab.ActiveRevisionId, "gpt-live-1", "first-visit", "empty", "other"));
            var evening = memory.GetStatus("device").EveningId;
            var ownedConversation = Guid.NewGuid().ToString("D");
            var otherConversation = Guid.NewGuid().ToString("D");
            var memoryId = Guid.NewGuid().ToString("D");
            memory.WriteMemory(new("device", evening, ownedConversation, PersonalityCatalog.OllivanderId),
                new(memoryId, MemoryScope.Conversation, MemoryKind.UserReportedFact, "private conversation", "private-source"));
            memory.WriteMemory(new("device", evening, otherConversation, PersonalityCatalog.OllivanderId),
                new(Guid.NewGuid().ToString("D"), MemoryScope.Conversation, MemoryKind.UserReportedFact, "other guest conversation", "other-source"));
            using (var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "journal.sqlite")))
            {
                connection.Open();
                for (var index = 0; index < 105; index++)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        INSERT INTO customer_sessions(id,owner_id,operation_id,device_id,boot_id,profile_id,profile_revision,
                            snapshot,state,conversation_id,created_at,updated_at)
                        VALUES($id,'alice',$id,'device','boot','profile',1,'private snapshot','completed',$conversation,0,0)
                        """;
                    command.Parameters.AddWithValue("$id", "session-" + index);
                    command.Parameters.AddWithValue("$conversation", index == 0 ? ownedConversation : Guid.NewGuid().ToString("D"));
                    command.ExecuteNonQuery();
                }
            }
            using (var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "journal.sqlite.evening.sqlite")))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO memory_indexes(memory_id,revision,store_id,filename,file_id,state)
                        VALUES($id,1,'fake-store','fake-filename','fake-file','indexed')
                    """;
                command.Parameters.AddWithValue("$id", memoryId);
                command.ExecuteNonQuery();
            }
            var photoId = Guid.NewGuid().ToString("D");
            Check(journal.ArchiveVisualMedia(new(photoId, "device", ownedConversation, 1, "phone", DateTimeOffset.UtcNow, [1,2,3])),
                "owned visual fixture archived");
            var owned = new List<string>();
            for (var index = 0; index < 105; index++)
            {
                var id = keepsakes.Issue(DeletionSnapshot(index.ToString()), new("alice", "session-" + index)).Id;
                keepsakes.RemoveCustomerAccountLink("alice", id);
                owned.Add(id);
            }
            var other = keepsakes.Issue(DeletionSnapshot("other"), new("bob", "bob-session")).Id;
            db.ArchiveAssociations.Add(new AccountArchiveAssociation { UserId = "alice", Locator = other });
            await db.SaveChangesAsync();
            var service = scope.ServiceProvider.GetRequiredService<AccountDeletionService>();
            var result = await service.RequestAsync("alice");
            Check(result.Status == AccountDeletionRequestStatus.Accepted && result.ReceiptId?.Length == 32,
                "deletion durably accepted with opaque receipt");
            Check((await db.Users.FindAsync("alice"))!.Disabled && !await db.Sessions.AnyAsync(x => x.UserId == "alice"),
                "accepted account is disabled and sessions revoked atomically");
            Check(await service.ReadStatusAsync(result.ReceiptId!) == "pending", "receipt starts pending");
            var missingExportServices = new ServiceCollection();
            missingExportServices.AddSingleton(journal);
            missingExportServices.AddSingleton(memory);
            missingExportServices.AddSingleton(characters);
            missingExportServices.AddSingleton(keepsakes);
            missingExportServices.AddSingleton(lab);
            missingExportServices.AddSingleton(conversations);
            using (var missingExportProvider = missingExportServices.BuildServiceProvider())
            {
                try
                {
                    await new AccountDeletionService(db, missingExportProvider, TimeProvider.System).RequestAsync("bob");
                    throw new InvalidOperationException("Account deletion accepted without the required export store.");
                }
                catch (AccountDeletionUnavailableException) { }
            }
            Check(!await db.Users.Where(user => user.Id == "bob").Select(user => user.Disabled).SingleAsync()
                && !await db.DeletionRequests.AnyAsync(request => request.UserId == "bob"),
                "missing export registration rejects deletion before disabling the account or recording intent");
            // Missing stores on a later process must leave the accepted durable request retryable.
            var missingServices = new ServiceCollection().BuildServiceProvider();
            await new AccountDeletionService(db, missingServices, TimeProvider.System).ProcessPendingAsync();
            Check(await service.ReadStatusAsync(result.ReceiptId!) == "pending", "failed cleanup retains pending intent");
            var blockedDirectory = Path.Combine(root, "archive", owned[0]);
            Directory.CreateDirectory(blockedDirectory);
            var blockedPath = Path.Combine(blockedDirectory, "private-photo.jpg");
            using (var blockedPhoto = new FileStream(blockedPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                blockedPhoto.Write([1,2,3]);
                if (OperatingSystem.IsWindows())
                {
                    await service.ProcessPendingAsync();
                    Check(await service.ReadStatusAsync(result.ReceiptId!) == "pending",
                        "filesystem cleanup failure cannot report completed deletion");
                }
            }
            using (var exportConnection = new SqliteConnection("Data Source=" + exports.DatabasePath))
            {
                exportConnection.Open();
                using var writeBlock = exportConnection.BeginTransaction(deferred: false);
                await service.ProcessPendingAsync();
                Check(await service.ReadStatusAsync(result.ReceiptId!) == "pending"
                    && exports.Find(aliceExport.Id, aliceExportAccess) is not null
                    && HasExportRequest(exports.DatabasePath, "alice", aliceOldRequest),
                    "busy export metadata database leaves deletion pending before its export cleanup commits");
            }
            using (var retryScope = provider.CreateScope())
            {
                await retryScope.ServiceProvider.GetRequiredService<AccountDeletionService>().ProcessPendingAsync();
            }
            Check(!Directory.Exists(blockedDirectory), "fresh worker retry physically removes owned photo directory");
            Check(await service.ReadStatusAsync(result.ReceiptId!) == "completed", "retry completes durable saga");
            Check(exports.Find(aliceExport.Id, aliceExportAccess) is null
                && !HasExportRequest(exports.DatabasePath, "alice", aliceOldRequest)
                && !HasExportRequest(exports.DatabasePath, "alice", aliceCurrentRequest),
                "original account export jobs and accepted-key receipts are purged after deletion fences");
            Check(exports.Find(bobExport.Id, bobExportAccess) is not null
                && exports.Create(bobExportAccess, bobExportRequest).Id == bobExport.Id,
                "deleting Alice's export metadata preserves Bob's job and request receipt");
            var deletedReplayRejected = false;
            try { exports.Create(aliceExportAccess, aliceCurrentRequest); }
            catch (AccountDataExportException error) when (error.Status == 410 && error.Code == "account_export_deleted")
            { deletedReplayRejected = true; }
            Check(deletedReplayRejected, "permanent export account fence rejects an in-flight captured access proof");
            Check(!exports.Complete(aliceExport.Id,
                    new("inventory", "memory", "keepsake", new string('A', 64)))
                && !exports.Fail(aliceExport.Id, AccountDataExportFailure.SourceChanged),
                "late export worker completion or failure cannot recreate deleted job metadata");
            var restartedExports = new AccountDataExportStore(Path.Combine(root, "accounts"), TimeProvider.System);
            var restartedDeletedReplayRejected = false;
            try { restartedExports.Create(aliceExportAccess, Guid.NewGuid()); }
            catch (AccountDataExportException error) when (error.Status == 410 && error.Code == "account_export_deleted")
            { restartedDeletedReplayRejected = true; }
            Check(restartedDeletedReplayRejected && restartedExports.Find(bobExport.Id, bobExportAccess) is not null,
                "permanent account fence survives store restart without affecting other accounts");
            await service.ProcessPendingAsync();
            Check(await service.ReadStatusAsync(result.ReceiptId!) == "completed",
                "reprocessing a completed deletion remains idempotently complete");
            Check(!await db.Users.AnyAsync(x => x.Id == "alice") && await db.Users.AnyAsync(x => x.Id == "bob"),
                "final identity deletion preserves other guests");
            Check(!await db.ArchiveAssociations.AnyAsync(x => x.UserId == "alice"), "access-only archive associations unlinked");
            Check(await characters.ReadAsync("alice") is null && await characters.ReadAsync("bob") is not null,
                "character ownership remains isolated");
            Check(journal.ReadVisualMedia("device", ownedConversation, photoId) is null
                && !journal.ArchiveVisualMedia(new(Guid.NewGuid().ToString("D"), "device", ownedConversation, 2, "phone", DateTimeOffset.UtcNow, [4,5])),
                "owned photo bytes erased and late distinct-image upload fenced");
            Check(memory.LocalMemory(new("device", evening, ownedConversation, PersonalityCatalog.OllivanderId)).Count == 0
                && memory.LocalMemory(new("device", evening, otherConversation, PersonalityCatalog.OllivanderId)).Count > 0,
                "all owned conversations deleted while another guest memory remains");
            using (var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "journal.sqlite.evening.sqlite")))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM memory_outbox WHERE action='delete' AND memory_id=$id";
                command.Parameters.AddWithValue("$id", memoryId);
                Check((long)command.ExecuteScalar()! > 0, "provider vector deletion queue survives account cleanup");
            }
            Check(lab.Sessions("alice").Length == 0 && lab.Session("bob", bobLab.Id).Id == bobLab.Id
                && lab.Revisions().Length > 0, "owned lab docs deleted and shared revisions preserved");
            using (var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "journal.sqlite")))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM dv10_keepsakes WHERE deleted=1";
                Check((long)command.ExecuteScalar()! == 105, "complete original-owner inventory includes revoked and more than 100 rows");
                command.CommandText = "SELECT deleted FROM dv10_keepsakes WHERE id=$id";
                command.Parameters.AddWithValue("$id", other);
                Check((long)command.ExecuteScalar()! == 0, "access to other guest archive never grants deletion ownership");
            }
            try { keepsakes.Issue(DeletionSnapshot("late"), new("alice", "late")); throw new Exception("Missing keepsake fence"); }
            catch (KeepsakeException error) when (error.Code == "account_deleted") { }
            try { lab.CreateSession("alice", new(lab.ActiveRevisionId, "gpt-live-1", "first-visit", "empty", "late")); throw new Exception("Missing lab fence"); }
            catch (LabException) { }
            try { lab.CreateSuite("alice", new(lab.ActiveRevisionId, "gpt-live-1", "empty", ["first-visit"], "late")); throw new Exception("Missing suite fence"); }
            catch (LabException) { }
            try { await characters.SaveAsync("alice", new(0, new CharacterUserInput("Late", "Original"))); throw new Exception("Missing character fence"); }
            catch (CharacterException) { }
            try
            {
                journal.IssueCustomerTicket("alice", new(1, "late-op", "device", "boot", "challenge", "late-profile", 1, OpenAiConsent: true),
                    "private", "late-hash", "late-protected");
                throw new Exception("Missing ticket fence");
            }
            catch (CustomerSessionException error) when (error.Status == 410) { }
            using var restart = provider.CreateScope();
            var restarted = restart.ServiceProvider.GetRequiredService<AccountDeletionService>();
            await restarted.ProcessPendingAsync();
            Check(await restarted.ReadStatusAsync(result.ReceiptId!) == "completed", "completed receipt survives fresh scope and idempotent replay");
            try { await new AccountDeletionService(db, missingServices, TimeProvider.System).RequestAsync("bob"); throw new Exception("Missing preflight"); }
            catch (AccountDeletionUnavailableException) { }
            Check(!await db.DeletionRequests.AnyAsync(x => x.UserId == "bob"), "preflight rejects unavailable stores before durable acceptance");
            db.Roles.Add(new IdentityRole { Id = "owner-role", Name = AccountRoles.Owner, NormalizedName = "OWNER" });
            db.Users.AddRange(new AccountUser { Id = "owner-a", UserName = "Owner A" }, new AccountUser { Id = "owner-b", UserName = "Owner B" });
            db.UserRoles.AddRange(new IdentityUserRole<string> { UserId = "owner-a", RoleId = "owner-role" },
                new IdentityUserRole<string> { UserId = "owner-b", RoleId = "owner-role" });
            await db.SaveChangesAsync();
            async Task<AccountDeletionRequestResult> DeleteOwnerAsync(string id)
            {
                using var ownerScope = provider.CreateScope();
                return await ownerScope.ServiceProvider.GetRequiredService<AccountDeletionService>().RequestAsync(id);
            }
            var outcomes = await Task.WhenAll(Task.Run(() => DeleteOwnerAsync("owner-a")), Task.Run(() => DeleteOwnerAsync("owner-b")));
            Check(outcomes.Count(x => x.Status == AccountDeletionRequestStatus.Accepted) == 1
                && outcomes.Count(x => x.Status == AccountDeletionRequestStatus.LastOwner) == 1,
                "concurrent owner deletion serializes and retains exactly one active owner");
            var pendingOwner = await db.DeletionRequests.AsNoTracking().SingleAsync(x => x.UserId.StartsWith("owner-"));
            using var adminScope = provider.CreateScope();
            var adminDb = adminScope.ServiceProvider.GetRequiredService<AccountDatabase>();
            var adminContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            adminContext.RequestServices = adminScope.ServiceProvider;
            var response = await AccountEndpoints.ChangeUserCoreAsync(pendingOwner.UserId,
                new(AccountRoles.Owner, false), adminContext, adminDb,
                adminScope.ServiceProvider.GetRequiredService<UserManager<AccountUser>>());
            Check(response is Microsoft.AspNetCore.Http.IStatusCodeHttpResult { StatusCode: 409 },
                "administrator cannot re-enable pending deletion");
            Console.WriteLine("Account deletion saga checks passed.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    private static KeepsakeIssueSnapshot DeletionSnapshot(string id)
        => new("account-delete-" + id, "device", 1, "wand", "Wand", "facts", "evidence");

    private static bool HasExportRequest(string databasePath, string accountId, Guid requestId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM account_data_export_requests WHERE account_id=$account AND request_id=$request";
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$request", requestId.ToString("N"));
        return command.ExecuteScalar() is not null;
    }
}
