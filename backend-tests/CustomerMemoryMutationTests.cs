using System.Security.Cryptography;
using System.Text;
using Kufr.Backend.Features.CustomerData;
using Kufr.Backend.Features.Memory;
using Kufr.Backend.Features.GptLive;
using Microsoft.Data.Sqlite;

internal static partial class AccountIdentityTests
{
    internal static async Task RunCustomerMemoryMutationsAsync()
    {
        await using var fixture = await Fixture.StartAsync(customerFeatures: true);
        using var owner = fixture.Client();
        using var ownerKey = new VirtualPasskey();
        await EnrollAsync(owner, ownerKey, Fixture.Bootstrap, "Mutation-test owner");
        await CsrfAsync(owner);

        using var clientA = fixture.Client();
        using var keyA = new VirtualPasskey();
        var profileA = await CustomerAsync(clientA, keyA);
        var accountA = (await CsrfAsync(clientA)).GetProperty("account").GetProperty("id").GetString()!;
        using var dataA = await CustomerMemoryHarness.CreateAsync(fixture.App, accountA, profileA);

        using var clientB = fixture.Client();
        using var keyB = new VirtualPasskey();
        var profileB = await CustomerAsync(clientB, keyB);
        var accountB = (await CsrfAsync(clientB)).GetProperty("account").GetProperty("id").GetString()!;
        using var dataB = await CustomerMemoryHarness.CreateAsync(fixture.App, accountB, profileB);

        SeedAllowedRecord(dataA, "original private fact", "answer-1");
        var original = dataA.Store.CurrentMemory(dataA.MemoryId)
            ?? throw new InvalidOperationException("mutation fixture fact was not stored");
        var before = await dataA.Source.CaptureAsync(await dataA.Authority.SnapshotAsync(dataA.Access));
        var corrected = await WithScopeAsync(dataA, scope => dataA.Guarded.Correct(scope, dataA.MemoryId,
            new(original.Revision, "corrected private fact")));
        var latest = dataA.Store.CurrentMemory(dataA.MemoryId)
            ?? throw new InvalidOperationException("corrected memory disappeared");
        Check(corrected.Id == dataA.MemoryId && corrected.Revision == original.Revision + 1
            && corrected.State == "corrected", "correction returns the new committed revision");
        Check(latest.Text == "corrected private fact" && latest.SourceReference == original.SourceReference
            && latest.Kind == MemoryKind.UserReportedFact && latest.DeviceId == original.DeviceId
            && latest.ConversationId == original.ConversationId && latest.GuestId == original.GuestId
            && latest.PermissionVersion == original.PermissionVersion && latest.ExportAllowed == original.ExportAllowed,
            "correction preserves the original owned tuple, kind, source, historical permission version, and export consent");
        Check(!await dataA.Source.IsCurrentAsync(await dataA.Authority.SnapshotAsync(dataA.Access), before),
            "successful correction invalidates a previously captured customer-memory snapshot");
        var oldRevision = ReadMemoryRevision(dataA, dataA.MemoryId, original.Revision);
        Check(oldRevision.Text == "original private fact" && !oldRevision.IsActive && !oldRevision.UseAllowed && !oldRevision.ExportAllowed,
            "correction retains the old revision while making it inactive and unusable");

        var stale = await CaptureCustomerMemoryErrorAsync(() => WithScopeAsync(dataA, scope =>
            dataA.Guarded.Correct(scope, dataA.MemoryId, new(original.Revision, "stale overwrite"))));
        Check(stale is { Status: 409, Code: "memory_revision_mismatch" }
            && dataA.Store.CurrentMemory(dataA.MemoryId)?.Text == "corrected private fact",
            "stale expected revision cannot overwrite the current fact");

        var selectionId = Guid.NewGuid().ToString("D");
        var selection = dataA.Store.WriteMemory(dataA.AccessFor(dataA.Guest), new(selectionId,
            MemoryScope.Personal, MemoryKind.ConfirmedSelection, "confirmed selection", "answer-3"));
        Check(selection is not null, "confirmed selection fixture is stored");
        var selectionCorrection = await CaptureCustomerMemoryErrorAsync(() => WithScopeAsync(dataA, scope =>
            dataA.Guarded.Correct(scope, selectionId, new(selection!.Revision, "untrusted replacement"))));
        Check(selectionCorrection is { Status: 404, Code: "memory_unavailable" }
            && dataA.Store.CurrentMemory(selectionId)?.Text == "confirmed selection",
            "confirmed selections cannot be corrected into user-reported facts");

        var foreign = await CaptureCustomerMemoryErrorAsync(() => WithScopeAsync(dataB, scope =>
            dataB.Guarded.Correct(scope, dataA.MemoryId, new(latest.Revision, "cross-account overwrite"))));
        Check(foreign is { Status: 404, Code: "memory_unavailable" }
            && dataA.Store.CurrentMemory(dataA.MemoryId)?.Text == "corrected private fact",
            "matching display names do not let another account mutate a customer memory");

        var foreignConversationId = InsertIneligibleMemory(dataA, conversation: "foreign-conversation",
            appNamespace: dataA.Options.Namespace);
        var foreignConversation = await CaptureCustomerMemoryErrorAsync(() => WithScopeAsync(dataA, scope =>
            dataA.Guarded.Forget(scope, foreignConversationId, new(1, Confirmed: true))));
        Check(foreignConversation is { Status: 404, Code: "memory_unavailable" }
            && dataA.Store.CurrentMemory(foreignConversationId)?.Text == "unowned tuple fixture",
            "a memory with a different conversation tuple cannot be forgotten through the retained session");

        var foreignNamespaceId = InsertIneligibleMemory(dataA, conversation: dataA.ConversationId,
            appNamespace: "foreign:namespace");
        var foreignNamespace = await CaptureCustomerMemoryErrorAsync(() => WithScopeAsync(dataA, scope =>
            dataA.Guarded.Forget(scope, foreignNamespaceId, new(1, Confirmed: true))));
        Check(foreignNamespace is { Status: 404, Code: "memory_unavailable" }
            && dataA.Store.CurrentMemory(foreignNamespaceId)?.Text == "unowned tuple fixture",
            "a memory from another application namespace cannot be forgotten");

        await VerifyQueuedRememberFencesAsync(dataA);
        await VerifyStaleIndexCompletionAsync(dataA);

        await VerifyCorrectionCapacityAndQuotaAsync(dataA);
        await VerifyMutationPreflightLimitsAsync(dataA);
        await VerifyForgetAsync(dataA, selectionId);
    }

    private static async Task VerifyQueuedRememberFencesAsync(CustomerMemoryHarness data)
    {
        const string correctionReference = "input-701";
        const string correctionRequest = "zapamatuj si queued fact after correction";
        var correctionId = RememberedMemoryId(data, correctionReference, correctionRequest);
        var correctionRecord = data.Store.WriteMemory(data.AccessFor(data.Guest), new(correctionId,
            MemoryScope.Personal, MemoryKind.UserReportedFact, "before queued correction", correctionReference))
            ?? throw new InvalidOperationException("queued-correction fixture fact was not stored");
        var correctionAccess = data.AccessFor(data.Guest);
        var factEpoch = data.Store.MemoryFactEpochFor(correctionAccess);
        var deletionEpoch = data.Store.MemoryDeletionEpochFor(data.DeviceId, data.ConversationId);
        var pendingCorrection = await WithScopeAsync(data, scope =>
        {
            var pending = data.Service.ChangePersonalMemoryAsync(correctionAccess, LiveRequestIntent.Remember,
                new(correctionReference, correctionRequest), CancellationToken.None, factEpoch, deletionEpoch);
            Check(!pending.IsCompleted, "remember request remains queued behind the active customer-memory admission lease");
            var updated = data.Guarded.Correct(scope, correctionId,
                new(correctionRecord.Revision, "corrected before queued remember"));
            Check(updated.Revision == correctionRecord.Revision + 1,
                "guarded correction commits while the ordinary remember write is queued");
            return pending;
        });
        var correctionResult = await pendingCorrection;
        var corrected = data.Store.CurrentMemory(correctionId)
            ?? throw new InvalidOperationException("queued-correction fact disappeared");
        Check(correctionResult.Status == "memory_write_unavailable" && corrected.Revision == correctionRecord.Revision + 1
            && corrected.Text == "corrected before queued remember",
            "queued remember write observes the newer fact epoch and cannot resurrect or overwrite a corrected fact");

        const string forgetReference = "input-702";
        const string forgetRequest = "zapamatuj si queued fact after forget";
        var forgetId = RememberedMemoryId(data, forgetReference, forgetRequest);
        var forgetRecord = data.Store.WriteMemory(data.AccessFor(data.Guest), new(forgetId,
            MemoryScope.Personal, MemoryKind.UserReportedFact, "before queued forget", forgetReference))
            ?? throw new InvalidOperationException("queued-forget fixture fact was not stored");
        var forgetAccess = data.AccessFor(data.Guest);
        factEpoch = data.Store.MemoryFactEpochFor(forgetAccess);
        deletionEpoch = data.Store.MemoryDeletionEpochFor(data.DeviceId, data.ConversationId);
        var pendingForget = await WithScopeAsync(data, scope =>
        {
            var pending = data.Service.ChangePersonalMemoryAsync(forgetAccess, LiveRequestIntent.Remember,
                new(forgetReference, forgetRequest), CancellationToken.None, factEpoch, deletionEpoch);
            Check(!pending.IsCompleted, "second remember request queues while the real admission lease is held");
            var forgotten = data.Guarded.Forget(scope, forgetId,
                new(forgetRecord.Revision, Confirmed: true));
            Check(forgotten.State == "forgotten", "guarded forget commits while the ordinary remember write is queued");
            return pending;
        });
        var forgetResult = await pendingForget;
        var erased = ReadMemoryRevision(data, forgetId, forgetRecord.Revision);
        Check(forgetResult.Status == "memory_write_unavailable" && erased.Text.Length == 0
            && !erased.IsActive && erased.ContentBytes == 0,
            "queued remember write observes the newer fact epoch and cannot resurrect a forgotten fact");
    }

    private static string RememberedMemoryId(CustomerMemoryHarness data, string reference, string sourceText)
    {
        var material = data.Options.Namespace + ":" + data.DeviceId + ":" + data.ConversationId
            + ":personal:" + data.Guest.Version + ":" + reference + ":" + sourceText;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new Guid(hash.AsSpan(0, 16)).ToString("D");
    }

    private static async Task VerifyCorrectionCapacityAndQuotaAsync(CustomerMemoryHarness data)
    {
        var current = data.Store.CurrentMemory(data.MemoryId)
            ?? throw new InvalidOperationException("capacity fixture fact is missing");
        var fillerId = Guid.NewGuid().ToString("D");
        InsertFillerRecord(data, fillerId, data.Options.MaxStoredBytes);
        var capacity = await CaptureCustomerMemoryErrorAsync(() => WithScopeAsync(data, scope =>
            data.Guarded.Correct(scope, data.MemoryId, new(current.Revision, "capacity should reject"))));
        var afterCapacity = data.Store.CurrentMemory(data.MemoryId)
            ?? throw new InvalidOperationException("capacity rejection removed the fact");
        Check(capacity is { Status: 413, Code: "archive_capacity" }
            && afterCapacity.Revision == current.Revision && afterCapacity.Text == current.Text,
            "capacity-rejected correction leaves the current revision and text unchanged");
        Check(data.Service.Status.RejectedWrites > 0 && data.Service.Status.LastStorageErrorCode == "archive_capacity",
            "capacity-rejected correction commits the archive diagnostic");
        DeleteRecord(data, fillerId);

        using (var connection = OpenFixtureDatabase(data))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO memory_usage(day,kind,amount) VALUES($day,'write',$amount) "
                + "ON CONFLICT(day,kind) DO UPDATE SET amount=excluded.amount";
            command.Parameters.AddWithValue("$day", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"));
            command.Parameters.AddWithValue("$amount", data.Options.MaxWritesPerDay);
            command.ExecuteNonQuery();
        }
        var quota = await CaptureCustomerMemoryErrorAsync(() => WithScopeAsync(data, scope =>
            data.Guarded.Correct(scope, data.MemoryId, new(current.Revision, "quota should reject"))));
        var afterQuota = data.Store.CurrentMemory(data.MemoryId)
            ?? throw new InvalidOperationException("quota rejection removed the fact");
        Check(quota is { Status: 429, Code: "memory_write_limit" }
            && afterQuota.Revision == current.Revision && afterQuota.Text == current.Text,
            "daily quota rejection leaves the current fact unchanged");
        using (var connection = OpenFixtureDatabase(data))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM memory_usage WHERE day=$day AND kind='write'";
            command.Parameters.AddWithValue("$day", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"));
            command.ExecuteNonQuery();
        }
    }

    private static async Task VerifyStaleIndexCompletionAsync(CustomerMemoryHarness data)
    {
        var indexId = Guid.NewGuid().ToString("D");
        data.Guest = data.Store.SetGuestPermission(data.DeviceId, data.ConversationId, data.Guest.GuestId,
            "verified_customer_account", true, true, true, data.Guest.Version)
            ?? throw new InvalidOperationException("fixture guest binding could not be enabled for index test");
        var access = data.AccessFor(data.Guest);
        var first = data.Store.WriteMemory(access, new(indexId, MemoryScope.Personal,
            MemoryKind.UserReportedFact, "index before correction", "answer-7"))
            ?? throw new InvalidOperationException("index fixture fact was not stored");
        var storeId = "fixture-owned-index-store";
        const string filename = "fixture-stale-completion-filename";
        using (var connection = OpenFixtureDatabase(data))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO memory_indexes(memory_id,revision,store_id,filename,file_id,state,owned) "
                + "VALUES($id,$revision,$store,$filename,NULL,'pending',1)";
            command.Parameters.AddWithValue("$id", indexId);
            command.Parameters.AddWithValue("$revision", first.Revision);
            command.Parameters.AddWithValue("$store", storeId);
            command.Parameters.AddWithValue("$filename", filename);
            command.ExecuteNonQuery();
        }
        var staleJob = new MemoryIndexJob(1, indexId, first.Revision, storeId, "index", 1, filename, null, "pending");
        var corrected = await WithScopeAsync(data, scope => data.Guarded.Correct(scope, indexId,
            new(first.Revision, "index after correction")));
        data.Store.SetMemoryIndex(staleJob, "completed", "fixture-late-file");
        Check(corrected.Revision == first.Revision + 1
            && data.Store.ResolveFile(storeId, "fixture-late-file", data.AccessFor(data.Guest), MemoryScope.Personal) is null,
            "late index completion cannot restore canonical search eligibility for a superseded revision");
        using var verify = OpenFixtureDatabase(data);
        using var pendingDelete = verify.CreateCommand();
        pendingDelete.CommandText = "SELECT COUNT(*) FROM memory_outbox WHERE memory_id=$id AND revision=$revision AND action='delete' AND state='pending'";
        pendingDelete.Parameters.AddWithValue("$id", indexId);
        pendingDelete.Parameters.AddWithValue("$revision", first.Revision);
        Check((long)pendingDelete.ExecuteScalar()! > 0,
            "a late completed cloud artifact receives a durable deletion request");
    }

    private static async Task VerifyForgetAsync(CustomerMemoryHarness data, string selectionId)
    {
        var record = data.Store.CurrentMemory(data.MemoryId)
            ?? throw new InvalidOperationException("forget fixture fact is missing");
        var unconfirmed = await CaptureCustomerMemoryErrorAsync(() => WithScopeAsync(data, scope =>
            data.Guarded.Forget(scope, data.MemoryId, new(record.Revision, Confirmed: false))));
        Check(unconfirmed is { Status: 400, Code: "memory_confirmation_required" }
            && data.Store.CurrentMemory(data.MemoryId)?.Text == record.Text,
            "forget requires explicit confirmation and leaves the fact intact when missing");

        var changed = data.Store.SetGuestPermission(data.DeviceId, data.ConversationId, data.Guest.GuestId,
            "verified_customer_account", true, false, false, data.Guest.Version)
            ?? throw new InvalidOperationException("fixture guest binding could not be updated");
        data.Guest = changed;
        var forgotten = await WithScopeAsync(data, scope =>
            data.Guarded.Forget(scope, data.MemoryId, new(record.Revision, Confirmed: true)));
        var erasedCurrent = ReadMemoryRevision(data, data.MemoryId, record.Revision);
        var erasedOld = ReadMemoryRevision(data, data.MemoryId, 1);
        Check(forgotten.State == "forgotten" && erasedCurrent.Text.Length == 0 && erasedCurrent.ContentBytes == 0
            && !erasedCurrent.IsActive && !erasedCurrent.UseAllowed && erasedOld.Text.Length == 0,
            "confirmed origin-bound forget erases every revision even after recall consent is withdrawn");
        Check(data.Store.CurrentMemory(selectionId)?.IsActive == true,
            "forgetting one record leaves another owned record intact");

        var blindId = Guid.NewGuid().ToString("D");
        var reenabled = data.Store.SetGuestPermission(data.DeviceId, data.ConversationId, data.Guest.GuestId,
            "verified_customer_account", true, true, false, data.Guest.Version)!;
        data.Guest = reenabled;
        var blind = data.Store.WriteMemory(data.AccessFor(data.Guest), new(blindId, MemoryScope.Personal,
            MemoryKind.UserReportedFact, "snapshot-unavailable fact", "answer-4"))
            ?? throw new InvalidOperationException("blind-forget fixture fact was not stored");
        data.SetSessionSnapshot("");
        var blindForgotten = await WithScopeAsync(data, scope =>
            data.Guarded.Forget(scope, blindId, new(blind.Revision, Confirmed: true)));
        Check(blindForgotten.State == "forgotten" && ReadMemoryRevision(data, blindId, blind.Revision).Text.Length == 0,
            "explicit forget remains available when the retained customer snapshot is unavailable");

        data.SetSessionSnapshot("synthetic customer snapshot");
        await data.Profiles.DeleteAsync(data.AccountId, data.ProfileRevision, data.ProfileId);
        var revoked = data.Store.CurrentMemory(selectionId)
            ?? throw new InvalidOperationException("selection fixture is missing after profile deletion");
        var revokedForgotten = await WithScopeAsync(data, scope => data.Guarded.Forget(scope,
            selectionId, new(revoked.Revision, Confirmed: true)));
        Check(revokedForgotten.State == "forgotten"
            && ReadMemoryRevision(data, selectionId, revoked.Revision).Text.Length == 0,
            "confirmed origin-bound forget still removes a selection after its profile is revoked");
    }

    private static async Task VerifyMutationPreflightLimitsAsync(CustomerMemoryHarness data)
    {
        data.SetSessionSnapshot("synthetic customer snapshot");
        var record = data.Store.CurrentMemory(data.MemoryId)
            ?? throw new InvalidOperationException("preflight fixture fact is missing");
        DeleteRevisions(data, record.MemoryId);
        InsertRevisionHistory(data, record.MemoryId, CustomerDataLimits.MaxMemoryItems);
        var history = await CaptureCustomerMemoryErrorAsync(() => WithScopeAsync(data, scope =>
            data.Guarded.Correct(scope, record.MemoryId,
                new(CustomerDataLimits.MaxMemoryItems, "history overflow"))));
        Check(history is { Status: 413, Code: "memory_mutation_limit" }
            && data.Store.CurrentMemory(record.MemoryId)?.Revision == CustomerDataLimits.MaxMemoryItems,
            "correction rejects a history that would exceed the bounded retained-revision limit");
        DeleteRevisions(data, record.MemoryId);

        SeedAllowedRecord(data, "index-overflow fact", "answer-8");
        var indexed = data.Store.CurrentMemory(data.MemoryId)
            ?? throw new InvalidOperationException("index-overflow fixture fact is missing");
        InsertIndexRows(data, indexed.MemoryId, CustomerDataLimits.MaxMemoryItems + 1);
        var indexOverflow = await CaptureCustomerMemoryErrorAsync(() => WithScopeAsync(data, scope =>
            data.Guarded.Forget(scope, indexed.MemoryId, new(indexed.Revision, Confirmed: true))));
        Check(indexOverflow is { Status: 413, Code: "memory_mutation_limit" }
            && data.Store.CurrentMemory(indexed.MemoryId)?.Text == indexed.Text,
            "forget fails closed before invalidating a record with an oversized index history");
        DeleteIndexes(data, indexed.MemoryId);

        SeedAllowedRecord(data, "binding-overflow fact", "answer-9");
        var bound = data.Store.CurrentMemory(data.MemoryId)
            ?? throw new InvalidOperationException("binding-overflow fixture fact is missing");
        InsertExtraBindings(data, CustomerDataLimits.MaxMemoryItems);
        var bindingOverflow = await CaptureCustomerMemoryErrorAsync(() => WithScopeAsync(data, scope =>
            data.Guarded.Correct(scope, bound.MemoryId, new(bound.Revision, "binding overflow"))));
        Check(bindingOverflow is { Status: 413, Code: "memory_mutation_limit" }
            && data.Store.CurrentMemory(bound.MemoryId)?.Text == bound.Text,
            "correction fails closed before mutation when the matching guest has unbounded bindings");
        DeleteExtraBindings(data);
    }

    private static async Task<CustomerDataAccessException?> CaptureCustomerMemoryErrorAsync<T>(Func<Task<T>> action)
    {
        try { _ = await action(); return null; }
        catch (CustomerDataAccessException error) { return error; }
    }

    private static MemoryRevisionFixture ReadMemoryRevision(CustomerMemoryHarness data, string id, long revision)
    {
        using var connection = OpenFixtureDatabase(data);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT text,is_active,use_allowed,export_allowed,content_bytes FROM memory_records "
            + "WHERE memory_id=$id AND revision=$revision";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$revision", revision);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) { throw new InvalidOperationException("expected memory revision was not retained"); }
        return new(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.GetInt64(4));
    }

    private static void InsertFillerRecord(CustomerMemoryHarness data, string id, long contentBytes)
    {
        using var connection = OpenFixtureDatabase(data);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO memory_records(memory_id,revision,scope,device_id,evening_id,conversation_id,guest_id,personality_id,kind,text,source_reference,created_at_ms,expires_at_ms,is_active,supersedes,use_allowed,export_allowed,permission_version,content_bytes,app_namespace) "
            + "VALUES($id,1,'Personal',$device,$evening,$conversation,$guest,'fixture','UserReportedFact','filler','answer-9',$now,$expires,1,NULL,1,0,$permission,$bytes,$namespace)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$device", data.DeviceId);
        command.Parameters.AddWithValue("$evening", data.EveningId);
        command.Parameters.AddWithValue("$conversation", data.ConversationId);
        command.Parameters.AddWithValue("$guest", data.Guest.GuestId);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$expires", DateTimeOffset.MaxValue.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$permission", data.Guest.Version);
        command.Parameters.AddWithValue("$bytes", contentBytes);
        command.Parameters.AddWithValue("$namespace", data.Options.Namespace);
        command.ExecuteNonQuery();
    }

    private static string InsertIneligibleMemory(CustomerMemoryHarness data, string conversation, string appNamespace)
    {
        var id = Guid.NewGuid().ToString("D");
        using var connection = OpenFixtureDatabase(data);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO memory_records(memory_id,revision,scope,device_id,evening_id,conversation_id,guest_id,personality_id,kind,text,source_reference,created_at_ms,expires_at_ms,is_active,supersedes,use_allowed,export_allowed,permission_version,content_bytes,app_namespace) "
            + "VALUES($id,1,'Personal',$device,$evening,$conversation,$guest,'fixture','UserReportedFact','unowned tuple fixture','answer-9',$now,$expires,1,NULL,1,0,$permission,22,$namespace)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$device", data.DeviceId);
        command.Parameters.AddWithValue("$evening", data.EveningId);
        command.Parameters.AddWithValue("$conversation", conversation);
        command.Parameters.AddWithValue("$guest", data.Guest.GuestId);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$expires", DateTimeOffset.MaxValue.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$permission", data.Guest.Version);
        command.Parameters.AddWithValue("$namespace", appNamespace);
        command.ExecuteNonQuery();
        return id;
    }

    private static void DeleteRecord(CustomerMemoryHarness data, string id)
    {
        using var connection = OpenFixtureDatabase(data);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM memory_records WHERE memory_id=$id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static void InsertRevisionHistory(CustomerMemoryHarness data, string id, int count)
    {
        using var connection = OpenFixtureDatabase(data);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO memory_records(memory_id,revision,scope,device_id,evening_id,conversation_id,guest_id,personality_id,kind,text,source_reference,created_at_ms,expires_at_ms,is_active,supersedes,use_allowed,export_allowed,permission_version,content_bytes,app_namespace) "
            + "VALUES($id,$revision,'Personal',$device,$evening,$conversation,$guest,'fixture','UserReportedFact','history','answer-1',$now,$expires,$active,$supersedes,$active,0,$permission,7,$namespace)";
        command.Parameters.Add("$id", SqliteType.Text).Value = id;
        command.Parameters.Add("$revision", SqliteType.Integer);
        command.Parameters.Add("$device", SqliteType.Text).Value = data.DeviceId;
        command.Parameters.Add("$evening", SqliteType.Text).Value = data.EveningId;
        command.Parameters.Add("$conversation", SqliteType.Text).Value = data.ConversationId;
        command.Parameters.Add("$guest", SqliteType.Text).Value = data.Guest.GuestId;
        command.Parameters.Add("$now", SqliteType.Integer).Value = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        command.Parameters.Add("$expires", SqliteType.Integer).Value = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();
        command.Parameters.Add("$active", SqliteType.Integer);
        command.Parameters.Add("$supersedes", SqliteType.Text);
        command.Parameters.Add("$permission", SqliteType.Integer).Value = data.Guest.Version;
        command.Parameters.Add("$namespace", SqliteType.Text).Value = data.Options.Namespace;
        for (var revision = 1; revision <= count; revision++)
        {
            command.Parameters["$revision"].Value = revision;
            command.Parameters["$active"].Value = revision == count ? 1 : 0;
            command.Parameters["$supersedes"].Value = revision == 1 ? DBNull.Value : $"{id}:{revision - 1}";
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void DeleteRevisions(CustomerMemoryHarness data, string id)
    {
        using var connection = OpenFixtureDatabase(data);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM memory_records WHERE memory_id=$id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static void InsertIndexRows(CustomerMemoryHarness data, string id, int count)
    {
        using var connection = OpenFixtureDatabase(data);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO memory_indexes(memory_id,revision,store_id,filename,state,owned) "
            + "VALUES($id,1,$store,$filename,'completed',1)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.Add("$store", SqliteType.Text);
        command.Parameters.Add("$filename", SqliteType.Text);
        for (var index = 0; index < count; index++)
        {
            command.Parameters["$store"].Value = $"fixture-store-{index}";
            command.Parameters["$filename"].Value = $"fixture-file-{id}-{index}";
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void DeleteIndexes(CustomerMemoryHarness data, string id)
    {
        using var connection = OpenFixtureDatabase(data);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM memory_indexes WHERE memory_id=$id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static void InsertExtraBindings(CustomerMemoryHarness data, int count)
    {
        using var connection = OpenFixtureDatabase(data);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO memory_guests(device_id,conversation_id,guest_id,identity_verified,identity_source,long_term_allowed,cloud_export_allowed,version,app_namespace) "
            + "VALUES($device,$conversation,$guest,1,'verified_customer_account',1,0,$version,$namespace)";
        command.Parameters.AddWithValue("$device", data.DeviceId);
        command.Parameters.AddWithValue("$guest", data.Guest.GuestId);
        command.Parameters.AddWithValue("$version", data.Guest.Version);
        command.Parameters.AddWithValue("$namespace", data.Options.Namespace);
        command.Parameters.Add("$conversation", SqliteType.Text);
        for (var index = 0; index < count; index++)
        {
            command.Parameters["$conversation"].Value = $"overflow-binding-{index}";
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void DeleteExtraBindings(CustomerMemoryHarness data)
    {
        using var connection = OpenFixtureDatabase(data);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM memory_guests WHERE device_id=$device AND conversation_id LIKE 'overflow-binding-%'";
        command.Parameters.AddWithValue("$device", data.DeviceId);
        command.ExecuteNonQuery();
    }

    private static SqliteConnection OpenFixtureDatabase(CustomerMemoryHarness data)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = data.Guarded.DatabasePath,
            DefaultTimeout = 2
        }.ToString());
        connection.Open();
        return connection;
    }

    private sealed record MemoryRevisionFixture(string Text, bool IsActive, bool UseAllowed,
        bool ExportAllowed, long ContentBytes);
}
