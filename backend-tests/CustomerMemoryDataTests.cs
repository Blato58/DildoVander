using System.Buffers;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.CustomerData;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.Memory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

internal static partial class AccountIdentityTests
{
    internal static async Task RunCustomerMemoryDataAsync()
    {
        await using var fixture = await Fixture.StartAsync(customerFeatures: true);
        using var owner = fixture.Client();
        using var ownerKey = new VirtualPasskey();
        await EnrollAsync(owner, ownerKey, Fixture.Bootstrap, "Customer-memory owner");
        await CsrfAsync(owner);
        using var customer = fixture.Client();
        using var key = new VirtualPasskey();
        var profile = await CustomerAsync(customer, key);
        var accountSession = await CsrfAsync(customer);
        var accountId = accountSession.GetProperty("account").GetProperty("id").GetString()!;
        var data = await CustomerMemoryHarness.CreateAsync(fixture.App, accountId, profile);
        using (data)
        {
            var inventory = await data.Authority.SnapshotAsync(data.Access);
            Check(inventory.Sessions.Count == 1 && inventory.Sessions[0].SessionId == data.CustomerSession.Id,
                "real customer session is included in complete guarded ownership inventory");

            SeedAllowedRecord(data, "allowed memory", "untrusted arbitrary source data");
            SeedSyntheticRecord(data, "other guest", guestId: "unrelated-guest", scope: "Personal",
                appNamespace: data.Options.Namespace, kind: "UserReportedFact");
            SeedSyntheticRecord(data, "conversation-only", guestId: data.Guest.GuestId, scope: "Conversation",
                appNamespace: data.Options.Namespace, kind: "UserReportedFact");
            SeedSyntheticRecord(data, "other namespace", guestId: data.Guest.GuestId, scope: "Personal",
                appNamespace: "other:namespace", kind: "UserReportedFact");
            var snapshot = await data.Source.CaptureAsync(inventory);
            Check(snapshot.Complete && snapshot.Items.Count == 1, "bounded local snapshot includes only exact owned personal origin");
            Check(snapshot.Items[0].Text == "allowed memory" && snapshot.Items[0].SourceReference == "",
                "local owned fact remains readable without cloud permission and unsafe source reference is removed");
            Check(await data.Source.IsCurrentAsync(inventory, snapshot), "captured full memory inventory is current");

            var changedPermission = data.Store.SetGuestPermission(data.DeviceId, data.ConversationId,
                data.Guest.GuestId, "verified_customer_account", true, true, true, data.Guest.Version)!;
            Check(!await data.Source.IsCurrentAsync(inventory, snapshot), "permission epoch change invalidates the captured memory snapshot");
            data.Guest = changedPermission;

            var revised = await data.Source.CaptureAsync(inventory);
            var existing = data.Store.CurrentMemory(data.MemoryId)!;
            var updated = data.Store.WriteMemory(data.AccessFor(data.Guest), new MemoryWrite(data.MemoryId,
                MemoryScope.Personal, MemoryKind.UserReportedFact, "updated allowed memory", "answer-2",
                ExpectedRevision: existing.Revision));
            Check(updated is not null && updated.Revision == 2, "owner fact correction creates a new retained revision");
            Check(!await data.Source.IsCurrentAsync(inventory, revised), "record revision change invalidates the captured memory snapshot");

            data.SeedOversizedMemoryRecord(oversizedText: true);
            var oversizedRecord = await CaptureFailureAsync(data, inventory);
            Check(oversizedRecord is CustomerDataAccessException { Status: 413, Code: "memory_inventory_limit" },
                "overlong persisted text fails closed before it is materialized");
            data.ClearOversizedMemoryRecord(oversizedText: true);
            data.SeedOversizedMemoryRecord(oversizedText: false);
            oversizedRecord = await CaptureFailureAsync(data, inventory);
            Check(oversizedRecord is CustomerDataAccessException { Status: 413, Code: "memory_inventory_limit" },
                "overlong persisted source reference fails closed before it is materialized");
            data.ClearOversizedMemoryRecord(oversizedText: false);

            await data.OverflowInventoryAsync();
            var overflow = await CaptureFailureAsync(data, inventory);
            Check(overflow is CustomerDataAccessException { Status: 413, Code: "memory_inventory_limit" },
                $"4097th eligible fact fails closed instead of truncating the inventory: {overflow?.GetType().Name}/{overflow?.Message}; eligible={data.CountEligible()}; options={data.Options.Namespace}; store={data.Store.CustomerMemoryNamespace}");
            data.ClearOverflowInventory();

            await ReleaseChecksAsync(data, inventory);
            await AdmissionChecksAsync(data);
            await FakeAdmissionRejectedAsync(data);

            var history = new CustomerMemoryDataSource(data.Authority, data.Guarded);
            var currentSnapshot = await history.CaptureAsync(inventory);
            var currentRecord = data.Store.CurrentMemory(data.MemoryId)!;
            Check(currentSnapshot.Items.Any(item => item.Id == data.MemoryId && item.Revision == currentRecord.Revision)
                && !currentSnapshot.Items.Any(item => item.Id == data.MemoryId && item.Revision < currentRecord.Revision),
                "snapshot exposes the current permitted revision and excludes inactive predecessors");

            data.SetSessionSnapshot("");
            var unavailableInventory = await data.Authority.SnapshotAsync(data.Access);
            Check(!unavailableInventory.Sessions[0].SnapshotAvailable
                && (await data.Source.CaptureAsync(unavailableInventory)).Items.Count == 0,
                "session with unavailable historical context contributes no personal-memory records");
            data.SetSessionSnapshot("synthetic customer snapshot");
            await data.Profiles.DeleteAsync(data.AccountId, data.ProfileRevision, data.ProfileId);
            var revokedInventory = await data.Authority.SnapshotAsync(data.Access);
            Check(revokedInventory.Sessions[0].ProfileRevoked
                && (await data.Source.CaptureAsync(revokedInventory)).Items.Count == 0,
                "revoked profile remains in ownership proof but contributes no local memory");
        }

        Console.WriteLine("PASS guarded customer memory: real owner/session scope, exact origin and namespace filters, cloud-independent local read, revision/permission invalidation, 4097 bound, admission contention/cancellation, exact/stale/oversize/cancel/expiry/uncertain release, and fake-admission rejection");
    }

    private static async Task ReleaseChecksAsync(CustomerMemoryHarness data, CustomerOwnershipInventory inventory)
    {
        var snapshot = await data.Source.CaptureAsync(inventory);
        var payload = Encoding.UTF8.GetBytes("fixture export bytes");
        var writer = new CountingBufferWriter();
        await WithScopeAsync(data, scope =>
        {
            data.Guarded.Release(scope, snapshot, payload, Convert.ToHexString(SHA256.HashData(payload)), writer);
            return 0;
        });
        Check(writer.AdvanceCount == 1 && writer.Written.SequenceEqual(payload), "release copies exact validated payload and advances exactly once");

        var forgedSnapshot = snapshot with
        {
            Items = Array.AsReadOnly(snapshot.Items.Select(item => item with { Text = item.Text + " forged" }).ToArray())
        };
        var forgedWriter = new CountingBufferWriter();
        var forgedError = await ReleaseFailureAsync(data, forgedSnapshot, payload,
            Convert.ToHexString(SHA256.HashData(payload)), forgedWriter);
        Check(forgedError is CustomerDataAccessException { Status: 409, Code: "memory_revision_mismatch" }
            && forgedWriter.AdvanceCount == 0, "release compares every projected item against the opaque current snapshot");

        var badHashWriter = new CountingBufferWriter();
        var badHash = await ReleaseFailureAsync(data, snapshot, payload, new string('0', 64), badHashWriter);
        Check(badHash is CustomerDataAccessException { Status: 409, Code: "memory_revision_mismatch" } && badHashWriter.AdvanceCount == 0,
            "wrong payload digest rejects before output Advance");

        var stale = await data.Source.CaptureAsync(inventory);
        SeedAllowedRecord(data, "concurrent revision", "answer-3");
        var staleWriter = new CountingBufferWriter();
        var staleError = await ReleaseFailureAsync(data, stale, payload, Convert.ToHexString(SHA256.HashData(payload)), staleWriter);
        Check(staleError is CustomerDataAccessException { Status: 409, Code: "memory_revision_mismatch" } && staleWriter.AdvanceCount == 0,
            "stale snapshot rejects before output Advance");
        var current = await data.Source.CaptureAsync(inventory);

        var oversizedWriter = new CountingBufferWriter();
        var oversized = new byte[CustomerDataLimits.MaxExportBytes + 1];
        var oversizedError = await ReleaseFailureAsync(data, current, oversized, Convert.ToHexString(SHA256.HashData(oversized)), oversizedWriter);
        Check(oversizedError is CustomerDataAccessException { Status: 413, Code: "export_size_limit" } && oversizedWriter.AdvanceCount == 0,
            "oversized release rejects before output Advance");

        var cancelWriter = new CountingBufferWriter();
        using (var cancellation = new CancellationTokenSource())
        {
            var cancelled = await ReleaseFailureAsync(data, current, payload,
                Convert.ToHexString(SHA256.HashData(payload)), cancelWriter, cancellation.Token,
                onGetMemory: cancellation.Cancel);
            Check(cancelled is OperationCanceledException && cancelWriter.AdvanceCount == 0,
                $"cancellation immediately before Advance prevents output mutation: {cancelled?.GetType().Name}/{cancelled?.Message}, advances={cancelWriter.AdvanceCount}");
        }

        var expiryWriter = new CountingBufferWriter(onGetMemory: () => Thread.Sleep(100));
        var expiryError = await WithScopeAsync(data, scope =>
        {
            scope.ConstrainDownloadExpiry(DateTimeOffset.UtcNow.AddMilliseconds(50));
            try
            {
                data.Guarded.Release(scope, current, payload, Convert.ToHexString(SHA256.HashData(payload)), expiryWriter);
                return null;
            }
            catch (Exception error) { return error; }
        });
        Check(expiryError is AccountDataExportException { Status: 410, Code: "export_download_expired" }
            && expiryWriter.AdvanceCount == 0, "download expiry immediately before Advance prevents output mutation");

        var uncertainWriter = new CountingBufferWriter(throwOnAdvance: true);
        var uncertain = await WithScopeAsync(data, scope =>
        {
            try
            {
                data.Guarded.Release(scope, current, payload, Convert.ToHexString(SHA256.HashData(payload)), uncertainWriter);
                return null;
            }
            catch (Exception error) { return error; }
        });
        Check(uncertain is CustomerDataReleaseUncertainException && uncertainWriter.AdvanceCount == 1,
            "an Advance that may have accepted bytes is surfaced as uncertain and never retried");
    }

    private static async Task<Exception?> ReleaseFailureAsync(CustomerMemoryHarness data,
        CustomerMemoryDataSnapshot snapshot, ReadOnlyMemory<byte> payload, string hash,
        CountingBufferWriter writer, CancellationToken token = default, Action? onGetMemory = null)
    {
        return await WithScopeAsync(data, scope =>
        {
            try
            {
                if (onGetMemory is not null) { writer.OnGetMemory = onGetMemory; }
                data.Guarded.Release(scope, snapshot, payload, hash, writer);
                return null;
            }
            catch (Exception error) { return error; }
        }, token);
    }

    private static async Task AdmissionChecksAsync(CustomerMemoryHarness data)
    {
        using var first = await data.Guarded.AcquireAsync(new CustomerDataGuardDeadline(CancellationToken.None));
        var second = await CaptureExceptionAsync(() => data.Guarded.AcquireAsync(new CustomerDataGuardDeadline(CancellationToken.None)));
        Check(second is { Status: 503, Code: "customer_data_unavailable" }, "contended memory writer admission fails within bounded wait");
        using var cancellation = new CancellationTokenSource();
        var cancelledWait = CaptureExceptionAsync(async () =>
        {
            using var unexpected = await data.Guarded.AcquireAsync(new CustomerDataGuardDeadline(cancellation.Token), cancellation.Token);
        });
        cancellation.CancelAfter(20);
        var cancellationError = await cancelledWait;
        Check(cancellationError is OperationCanceledException, "cancelled admission wait propagates cancellation without acquiring a lease");
        first.Dispose();
        using var recovered = await data.Guarded.AcquireAsync(new CustomerDataGuardDeadline(CancellationToken.None));
    }

    private static async Task FakeAdmissionRejectedAsync(CustomerMemoryHarness data)
    {
        Exception? error;
        using (var prelude = await data.Authority.AcquireAsync(data.Access))
        using (var scope = prelude.AcquireShared(new FakeMemoryLease()))
        {
            error = CaptureException(() => data.Guarded.Capture(scope));
        }
        Check(error is CustomerDataAccessException { Status: 503, Code: "customer_data_unavailable" }, "guarded read rejects a fake or foreign memory admission lease");

        var foreignOwner = new CustomerMemoryData(data.Store, data.Service);
        Exception? foreignError;
        using (var secondPrelude = await data.Authority.AcquireAsync(data.Access))
        using (var foreignLease = await foreignOwner.AcquireAsync(secondPrelude.Deadline))
        using (var foreignScope = secondPrelude.AcquireShared(foreignLease))
        {
            foreignError = CaptureException(() => data.Guarded.Capture(foreignScope));
        }
        Check(foreignError is CustomerDataAccessException { Status: 503, Code: "customer_data_unavailable" },
            "guarded read rejects a genuine archive lease owned by another guarded-data source instance");
    }

    private static async Task<T> WithScopeAsync<T>(CustomerMemoryHarness data,
        Func<CustomerDataGuardScope, T> action, CancellationToken token = default)
    {
        using var prelude = await data.Authority.AcquireAsync(data.Access, token);
        using var admission = await data.Guarded.AcquireAsync(prelude.Deadline, token);
        using var scope = prelude.AcquireShared(admission);
        return action(scope);
    }

    private static async Task<Exception?> CaptureFailureAsync(CustomerMemoryHarness data, CustomerOwnershipInventory inventory)
    {
        try
        {
            await data.Source.CaptureAsync(inventory);
            return null;
        }
        catch (Exception error) { return error; }
    }

    private static Exception? CaptureException(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }

    private static async Task<Exception?> CaptureExceptionAsync(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error; }
    }

    private static void SeedAllowedRecord(CustomerMemoryHarness data, string text, string sourceReference)
    {
        var old = data.Store.CurrentMemory(data.MemoryId);
        var record = data.Store.WriteMemory(data.AccessFor(data.Guest), new MemoryWrite(data.MemoryId,
            MemoryScope.Personal, MemoryKind.UserReportedFact, text, sourceReference,
            ExpectedRevision: old?.Revision ?? 0));
        Check(record is not null, "fixture personal memory record persists locally");
    }

    private static void SeedSyntheticRecord(CustomerMemoryHarness data, string text, string guestId,
        string scope, string appNamespace, string kind)
    {
        var recordId = Guid.NewGuid().ToString("D");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = data.Guarded.DatabasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memory_records(memory_id,revision,scope,device_id,evening_id,conversation_id,guest_id,personality_id,
                kind,text,source_reference,created_at_ms,expires_at_ms,is_active,supersedes,use_allowed,export_allowed,
                permission_version,content_bytes,app_namespace)
            VALUES($id,1,$scope,$device,$evening,$conversation,$guest,'fixture',$kind,$text,'answer-1',$created,$expires,
                1,NULL,1,0,$permission,$bytes,$namespace)
            """;
        command.Parameters.AddWithValue("$id", recordId);
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$device", data.DeviceId);
        command.Parameters.AddWithValue("$evening", data.EveningId);
        command.Parameters.AddWithValue("$conversation", data.ConversationId);
        command.Parameters.AddWithValue("$guest", guestId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$expires", DateTimeOffset.MaxValue.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$permission", data.Guest.Version);
        command.Parameters.AddWithValue("$bytes", Encoding.UTF8.GetByteCount(text));
        command.Parameters.AddWithValue("$namespace", appNamespace);
        command.ExecuteNonQuery();
    }

    private static async Task<CustomerDataAccessException?> CaptureExceptionAsync(Func<Task<ICustomerDataMemoryLease>> action)
    {
        try { using var lease = await action(); return null; }
        catch (CustomerDataAccessException error) { return error; }
    }

    private sealed class CustomerMemoryHarness : IDisposable
    {
        private readonly OpenAiVectorMemoryProvider provider;
        private readonly IServiceScopeFactory scopes;
        private readonly IOptions<IdentityOptions> identityOptions;

        private CustomerMemoryHarness(WebApplication app, string accountId, JsonElement profile,
            OpenAiVectorMemoryProvider provider, HybridMemoryOptions options, EveningMemoryStore store,
            OperationJournal journal, HybridMemoryService service, CustomerMemoryData guarded,
            AccountDataExportAuthority authority, CustomerDataAccess access, CustomerSessionRecord customerSession,
            GuestMemoryPermission guest, IServiceScopeFactory scopes, IOptions<IdentityOptions> identityOptions)
        {
            this.provider = provider;
            this.scopes = scopes;
            this.identityOptions = identityOptions;
            AccountId = accountId;
            ProfileId = profile.GetProperty("profileId").GetString()!;
            ProfileRevision = profile.GetProperty("revision").GetInt64();
            Store = store;
            Journal = journal;
            Service = service;
            Guarded = guarded;
            Authority = authority;
            Access = access;
            CustomerSession = customerSession;
            DeviceId = customerSession.DeviceId;
            ConversationId = customerSession.ConversationId!;
            EveningId = store.GetStatus(DeviceId).EveningId;
            Guest = guest;
            MemoryId = Guid.NewGuid().ToString("D");
            Options = options;
            Source = new CustomerMemoryDataSource(authority, guarded);
            Profiles = app.Services.GetRequiredService<CharacterProfileStore>();
        }

        internal string AccountId { get; }
        internal string ProfileId { get; }
        internal long ProfileRevision { get; }
        internal EveningMemoryStore Store { get; }
        internal OperationJournal Journal { get; }
        internal HybridMemoryService Service { get; }
        internal CustomerMemoryData Guarded { get; }
        internal AccountDataExportAuthority Authority { get; }
        internal CustomerDataAccess Access { get; }
        internal CustomerSessionRecord CustomerSession { get; }
        internal string DeviceId { get; }
        internal string ConversationId { get; }
        internal string EveningId { get; }
        internal GuestMemoryPermission Guest { get; set; }
        internal string MemoryId { get; }
        internal HybridMemoryOptions Options { get; }
        internal CustomerMemoryDataSource Source { get; }
        internal CharacterProfileStore Profiles { get; }

        internal MemoryAccess AccessFor(GuestMemoryPermission guest) =>
            new(DeviceId, EveningId, ConversationId, "ollivander", guest);

        internal static async Task<CustomerMemoryHarness> CreateAsync(WebApplication app, string accountId, JsonElement profile)
        {
            var services = app.Services;
            var journal = services.GetRequiredService<OperationJournal>();
            var store = services.GetRequiredService<EveningMemoryStore>();
            var memoryNamespace = store.CustomerMemoryNamespace.Split(':', 2);
            app.Configuration["Backend:Memory:AppNamespace"] = memoryNamespace[0];
            app.Configuration["Backend:Memory:Environment"] = memoryNamespace.Length > 1 ? memoryNamespace[1] : "Testing";
            app.Configuration["Backend:Memory:MaxStoredBytes"] = "67108864";
            var options = HybridMemoryOptions.Read(app.Configuration, journal);
            var provider = new OpenAiVectorMemoryProvider(new HttpClient(new OfflineProviderHandler()), null,
                options, ownsHttp: true);
            var service = new HybridMemoryService(store, provider, options, TimeProvider.System);
            var guarded = new CustomerMemoryData(store, service);
            var profiles = services.GetRequiredService<CharacterProfileStore>();
            var keepsakes = services.GetRequiredService<KeepsakeStore>();
            var exportJobs = services.GetService<AccountDataExportStore>()
                ?? new AccountDataExportStore(services.GetRequiredService<AccountConfiguration>().DataRoot,
                    services.GetRequiredService<TimeProvider>());
            var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
            var identityOptions = services.GetRequiredService<IOptions<IdentityOptions>>();
            var authority = new AccountDataExportAuthority(scopeFactory, journal, profiles, keepsakes,
                guarded, TimeProvider.System, identityOptions, exportJobs);

            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
                var user = await db.Users.AsNoTracking().SingleAsync(candidate => candidate.Id == accountId);
                var accountSession = await db.Sessions.AsNoTracking().SingleAsync(candidate => candidate.UserId == accountId);
                var stampClaim = identityOptions.Value.ClaimsIdentity.SecurityStampClaimType;
                var claims = new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, accountId),
                    new Claim(AccountIdentity.SessionClaim, accountSession.Id),
                    new Claim(stampClaim, user.SecurityStamp!)
                };
                var context = new DefaultHttpContext { RequestServices = services };
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "fixture-cookie"));
                var access = await authority.CaptureAsync(context);

                var deviceId = "customer-memory-owned-" + accountId[..8];
                var bootId = Guid.NewGuid().ToString("D");
                var challengeText = Guid.NewGuid().ToString("D");
                var operationId = Guid.NewGuid().ToString("D");
                journal.SetCustomerMode(deviceId, true);
                journal.RegisterCustomerChallenge(deviceId, new CustomerDeviceChallenge(1, bootId, challengeText,
                    0, PartyWandCatalog.Version, "1"));
                var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                var request = new CustomerTicketRequest(1, operationId, deviceId, bootId, challengeText,
                    profile.GetProperty("profileId").GetString()!, profile.GetProperty("revision").GetInt64(),
                    PersonalMemoryConsent: true, OpenAiConsent: true);
                journal.IssueCustomerTicket(accountId, request, "synthetic customer snapshot",
                    OperationJournal.CustomerHash(token), "protected fixture token");
                var customerSession = journal.RedeemCustomerTicket(deviceId,
                    new CustomerRedemption(1, token, operationId, bootId, challengeText));
                var create = journal.ReserveCreate(deviceId, Guid.Parse(operationId), "fixture-request-hash",
                    customerSessionId: customerSession.Id, bootId: bootId);
                if (create?.Response is null) { throw new InvalidOperationException("Customer memory test could not create its owned conversation."); }
                var conversationId = create.Response.ConversationId;
                var expectedGuest = CustomerMemoryOriginPolicy.ExpectedGuestId(accountId, request.ProfileId);
                var guest = store.SetGuestPermission(deviceId, conversationId, expectedGuest,
                    "verified_customer_account", true, true, false, 0)
                    ?? throw new InvalidOperationException("Customer memory test could not bind its verified guest.");

                return new CustomerMemoryHarness(app, accountId, profile, provider, options, store, journal,
                    service, guarded, authority, access, customerSession with { ConversationId = conversationId },
                    guest, scopeFactory, identityOptions);
            }
        }

        internal async Task OverflowInventoryAsync()
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Guarded.DatabasePath }.ToString());
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();
            for (var index = 0; index <= CustomerDataLimits.MaxMemoryItems; index++)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO memory_records(memory_id,revision,scope,device_id,evening_id,conversation_id,guest_id,personality_id,
                        kind,text,source_reference,created_at_ms,expires_at_ms,is_active,supersedes,use_allowed,export_allowed,
                        permission_version,content_bytes,app_namespace)
                    VALUES($id,1,'Personal',$device,$evening,$conversation,$guest,'fixture','UserReportedFact','x','capacity-fixture',
                        $created,$expires,1,NULL,1,0,$permission,1,$namespace)
                    """;
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                command.Parameters.AddWithValue("$device", DeviceId);
                command.Parameters.AddWithValue("$evening", EveningId);
                command.Parameters.AddWithValue("$conversation", ConversationId);
                command.Parameters.AddWithValue("$guest", Guest.GuestId);
                command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$expires", DateTimeOffset.MaxValue.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$permission", Guest.Version);
                command.Parameters.AddWithValue("$namespace", Options.Namespace);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        internal void SeedOversizedMemoryRecord(bool oversizedText)
        {
            var text = oversizedText ? new string('x', CustomerMemoryMaxRecordBytes + 1) : "ordinary text";
            var source = oversizedText ? "answer-1" : new string('s', 257);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Guarded.DatabasePath }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO memory_records(memory_id,revision,scope,device_id,evening_id,conversation_id,guest_id,personality_id,
                    kind,text,source_reference,created_at_ms,expires_at_ms,is_active,supersedes,use_allowed,export_allowed,
                    permission_version,content_bytes,app_namespace)
                VALUES($id,1,'Personal',$device,$evening,$conversation,$guest,'fixture','UserReportedFact',$text,$source,
                    $created,$expires,1,NULL,1,0,$permission,$bytes,$namespace)
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$device", DeviceId);
            command.Parameters.AddWithValue("$evening", EveningId);
            command.Parameters.AddWithValue("$conversation", ConversationId);
            command.Parameters.AddWithValue("$guest", Guest.GuestId);
            command.Parameters.AddWithValue("$text", text);
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$expires", DateTimeOffset.MaxValue.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$permission", Guest.Version);
            command.Parameters.AddWithValue("$bytes", Encoding.UTF8.GetByteCount(text));
            command.Parameters.AddWithValue("$namespace", Store.CustomerMemoryNamespace);
            command.ExecuteNonQuery();
        }

        internal void ClearOversizedMemoryRecord(bool oversizedText)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Guarded.DatabasePath }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM memory_records WHERE device_id=$device AND conversation_id=$conversation AND "
                + (oversizedText ? "length(CAST(text AS BLOB))>$maxBytes" : "length(CAST(source_reference AS BLOB))>256");
            command.Parameters.AddWithValue("$device", DeviceId);
            command.Parameters.AddWithValue("$conversation", ConversationId);
            if (oversizedText) { command.Parameters.AddWithValue("$maxBytes", CustomerMemoryMaxRecordBytes); }
            command.ExecuteNonQuery();
        }

        private int CustomerMemoryMaxRecordBytes => Store.CustomerMemoryMaxRecordBytes;

        internal void ClearOverflowInventory()
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Guarded.DatabasePath }.ToString());
            connection.Open();
            using var cleanup = connection.CreateCommand();
            cleanup.CommandText = "DELETE FROM memory_records WHERE source_reference='capacity-fixture'";
            cleanup.ExecuteNonQuery();
        }

        internal long CountEligible()
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Guarded.DatabasePath }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM memory_records WHERE app_namespace=$namespace AND scope='Personal' AND device_id=$device AND conversation_id=$conversation AND guest_id=$guest AND is_active=1 AND use_allowed=1 AND kind IN ('UserReportedFact','ConfirmedSelection')";
            command.Parameters.AddWithValue("$namespace", Options.Namespace);
            command.Parameters.AddWithValue("$device", DeviceId);
            command.Parameters.AddWithValue("$conversation", ConversationId);
            command.Parameters.AddWithValue("$guest", Guest.GuestId);
            return (long)command.ExecuteScalar()!;
        }

        internal void SetSessionSnapshot(string value)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Journal.CustomerDataDatabasePath }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE customer_sessions SET snapshot=$snapshot WHERE id=$id";
            command.Parameters.AddWithValue("$snapshot", value);
            command.Parameters.AddWithValue("$id", CustomerSession.Id);
            command.ExecuteNonQuery();
        }

        public void Dispose() => provider.Dispose();
    }

    private sealed class OfflineProviderHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Customer memory guarded-data tests must never call an external provider.");
    }

    private sealed class FakeMemoryLease : ICustomerDataMemoryLease
    {
        public void ValidateCurrent() { }
        public void Dispose() { }
    }

    private sealed class CountingBufferWriter(Action? onGetMemory = null, bool throwOnAdvance = false) : IBufferWriter<byte>
    {
        private byte[] buffer = new byte[1];
        internal Action? OnGetMemory { get; set; } = onGetMemory;
        internal int AdvanceCount { get; private set; }
        internal byte[] Written { get; private set; } = [];

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            OnGetMemory?.Invoke();
            if (buffer.Length < sizeHint) { buffer = new byte[sizeHint]; }
            return buffer;
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

        public void Advance(int count)
        {
            AdvanceCount++;
            if (throwOnAdvance) { throw new InvalidOperationException("fixture output accepted bytes then failed"); }
            Written = buffer.AsSpan(0, count).ToArray();
        }
    }
}
