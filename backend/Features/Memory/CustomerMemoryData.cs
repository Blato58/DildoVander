using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.CustomerData;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Memory;

internal sealed partial class CustomerMemoryData(EveningMemoryStore store, HybridMemoryService service)
    : ICustomerMemoryGuardedDataSource
{
    private static readonly Regex SafeSourceReference = new(
        @"^(?:answer|input)-[0-9]{1,9}$|^outcome:[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}:operation:[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}:completed_by_timing$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public string DatabasePath => store.CustomerMemoryDatabasePath;

    public async Task<ICustomerDataMemoryLease> AcquireAsync(
        CustomerDataGuardDeadline deadline,
        CancellationToken token = default)
    {
        deadline.Check();
        token.ThrowIfCancellationRequested();

        var lease = await service.TryAcquireCustomerMemoryWritesAsync(token);
        if (lease is null)
        {
            throw new CustomerDataAccessException(503, "customer_data_unavailable");
        }

        try
        {
            deadline.Check();
            token.ThrowIfCancellationRequested();
            return new CustomerMemoryGuard(this, service, deadline, token, lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public CustomerMemoryDataSnapshot Capture(CustomerDataGuardScope scope)
    {
        var guard = RequireGuard(scope);
        using var gateLease = EnterMemoryGate(scope);
        using var database = OpenMemoryDatabase();
        scope.ValidateCurrent();
        var snapshot = Capture(database.Connection, database.Transaction, scope);
        scope.ValidateCurrent();
        guard.ValidateCurrent();
        return snapshot;
    }

    public bool IsCurrent(CustomerDataGuardScope scope, CustomerMemoryDataSnapshot snapshot)
    {
        var guard = RequireGuard(scope);
        if (!snapshot.Complete || !scope.Inventory.Complete
            || snapshot.InventoryRevision != scope.Inventory.Revision)
        {
            return false;
        }

        using var gateLease = EnterMemoryGate(scope);
        using var database = OpenMemoryDatabase();
        scope.ValidateCurrent();
        var current = Capture(database.Connection, database.Transaction, scope);
        scope.ValidateCurrent();
        guard.ValidateCurrent();
        return SameSnapshot(current, snapshot);
    }

    public void Release(
        CustomerDataGuardScope scope,
        CustomerMemoryDataSnapshot snapshot,
        ReadOnlyMemory<byte> payload,
        string payloadSha256,
        IBufferWriter<byte> output)
    {
        var guard = RequireGuard(scope);
        if (payload.Length > CustomerDataLimits.MaxExportBytes)
        {
            throw new CustomerDataAccessException(413, "export_size_limit");
        }
        if (!MatchesSha256(payload.Span, payloadSha256))
        {
            throw new CustomerDataAccessException(409, "memory_revision_mismatch");
        }

        var advanceAttempted = false;
        try
        {
            using var gateLease = EnterMemoryGate(scope);
            using var database = OpenMemoryDatabase();
            scope.ValidateCurrent();
            var current = Capture(database.Connection, database.Transaction, scope);
            if (!SameSnapshot(current, snapshot))
            {
                throw new CustomerDataAccessException(409, "memory_revision_mismatch");
            }

            scope.ValidateCurrent();
            guard.ValidateCurrent();
            var memory = output.GetMemory(payload.Length);
            if (memory.Length < payload.Length)
            {
                throw new InvalidOperationException("Output writer returned less memory than requested.");
            }
            payload.Span.CopyTo(memory.Span);
            scope.ValidateCurrent();
            guard.ValidateCurrent();
            advanceAttempted = true;
            output.Advance(payload.Length);
        }
        catch (Exception error) when (advanceAttempted)
        {
            throw new CustomerDataReleaseUncertainException(error);
        }
    }

    private CustomerMemoryGuard RequireGuard(CustomerDataGuardScope scope)
    {
        scope.ValidateCurrent();
        if (scope.MemoryAdmission is not CustomerMemoryGuard guard || !guard.BelongsTo(this, service))
        {
            throw new CustomerDataAccessException(503, "customer_data_unavailable");
        }
        guard.ValidateCurrent();
        return guard;
    }

    private CustomerMemoryStoreGate EnterMemoryGate(CustomerDataGuardScope scope)
    {
        scope.ValidateCurrent();
        if (!store.TryEnterCustomerMemoryGate(TimeSpan.FromMilliseconds(100)))
        {
            scope.ValidateCurrent();
            throw new CustomerDataAccessException(503, "customer_data_unavailable");
        }

        try
        {
            scope.ValidateCurrent();
            return new CustomerMemoryStoreGate(store);
        }
        catch
        {
            store.ExitCustomerMemoryGate();
            throw;
        }
    }

    private CustomerMemoryDataSnapshot Capture(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CustomerDataGuardScope scope)
    {
        if (!scope.Inventory.Complete)
        {
            throw new CustomerDataAccessException(409, "customer_data_unavailable");
        }

        var access = scope.Inventory.Access;
        var candidates = new List<(CustomerMemoryReadProof Proof, CustomerMemoryItem Item)>();
        var origins = new List<CustomerMemoryOriginState>();
        var candidateCount = 0;
        var sessions = scope.Inventory.Sessions
            .OrderBy(session => session.ProfileId, StringComparer.Ordinal)
            .ThenBy(session => session.DeviceId, StringComparer.Ordinal)
            .ThenBy(session => session.ConversationId, StringComparer.Ordinal)
            .ThenBy(session => session.SessionId, StringComparer.Ordinal)
            .ToArray();

        long globalEpoch;
        long globalDeletionEpoch;
        using (var epochs = EveningMemoryStore.CustomerMemoryCommand(connection, transaction,
            "SELECT epoch,deletion_epoch FROM memory_meta WHERE id=1"))
        using (var epochReader = epochs.ExecuteReader())
        {
            if (!epochReader.Read()) { throw new CustomerDataAccessException(503, "customer_data_unavailable"); }
            globalEpoch = epochReader.GetInt64(0);
            globalDeletionEpoch = epochReader.GetInt64(1);
        }

        var seenOrigins = new HashSet<(string Profile, string Device, string Conversation, string Guest)>();
        foreach (var owned in sessions)
        {
            scope.ValidateCurrent();
            var conversation = owned.ConversationId;
            var usable = !owned.ProfileRevoked && owned.SnapshotAvailable
                && !string.IsNullOrWhiteSpace(owned.ProfileId)
                && !string.IsNullOrWhiteSpace(owned.DeviceId)
                && !string.IsNullOrWhiteSpace(conversation);
            var expectedGuest = usable
                ? CustomerMemoryOriginPolicy.ExpectedGuestId(access.AccountId, owned.ProfileId)
                : "";
            var binding = usable ? store.CustomerMemoryReadGuest(connection, transaction, owned.DeviceId, conversation!) : null;
            var factEpoch = usable
                ? store.CustomerMemoryConversationEpoch(connection, transaction, owned.DeviceId, conversation!, facts: true)
                : 0;
            var deletionEpoch = usable
                ? store.CustomerMemoryConversationEpoch(connection, transaction, owned.DeviceId, conversation!, facts: false)
                : 0;
            origins.Add(new CustomerMemoryOriginState(owned, expectedGuest, binding, factEpoch, deletionEpoch));

            if (!usable || !store.CustomerMemoryEnabled || !store.CustomerLongTermMemoryEnabled
                || !string.Equals(owned.ExpectedGuestId, expectedGuest, StringComparison.Ordinal)
                || binding is not { IdentityVerified: true, LongTermAllowed: true }
                || !string.Equals(binding.GuestId, expectedGuest, StringComparison.Ordinal)
                || !string.Equals(binding.DeviceId, owned.DeviceId, StringComparison.Ordinal)
                || !string.Equals(binding.ConversationId, conversation, StringComparison.Ordinal))
            {
                continue;
            }

            var originKey = (owned.ProfileId, owned.DeviceId, conversation!, expectedGuest);
            if (!seenOrigins.Add(originKey)) { continue; }

            var remaining = CustomerDataLimits.MaxMemoryItems - candidateCount;
            var limit = remaining + 1;
            using (var oversized = EveningMemoryStore.CustomerMemoryCommand(connection, transaction, """
                SELECT 1 FROM memory_records
                WHERE app_namespace=$namespace AND scope='Personal'
                    AND device_id=$device AND conversation_id=$conversation AND guest_id=$guest
                    AND is_active=1 AND use_allowed=1
                    AND kind IN ('UserReportedFact','ConfirmedSelection')
                    AND (length(CAST(text AS BLOB))>$maxTextBytes
                        OR length(CAST(source_reference AS BLOB))>256)
                LIMIT 1;
                """, ("$namespace", store.CustomerMemoryNamespace), ("$device", owned.DeviceId),
                ("$conversation", conversation), ("$guest", expectedGuest),
                ("$maxTextBytes", store.CustomerMemoryMaxRecordBytes)))
            {
                if (oversized.ExecuteScalar() is not null)
                {
                    throw new CustomerDataAccessException(413, "memory_inventory_limit");
                }
            }

            using var command = EveningMemoryStore.CustomerMemoryCommand(connection, transaction, """
                SELECT * FROM memory_records
                WHERE app_namespace=$namespace AND scope='Personal'
                    AND device_id=$device AND conversation_id=$conversation AND guest_id=$guest
                    AND is_active=1 AND use_allowed=1
                    AND kind IN ('UserReportedFact','ConfirmedSelection')
                ORDER BY memory_id,revision
                LIMIT $limit;
                """, ("$namespace", store.CustomerMemoryNamespace), ("$device", owned.DeviceId),
                ("$conversation", conversation), ("$guest", expectedGuest), ("$limit", limit));
            var rows = new List<MemoryRecord>(limit);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    scope.ValidateCurrent();
                    rows.Add(EveningMemoryStore.CustomerMemoryReadRecord(reader));
                }
            }

            if (rows.Count > remaining)
            {
                throw new CustomerDataAccessException(413, "memory_inventory_limit");
            }
            candidateCount += rows.Count;

            foreach (var record in rows)
            {
                scope.ValidateCurrent();
                var proof = CustomerMemoryReadPolicy.TryProve(
                    access.AccountId, access, owned, record, binding, store.CustomerMemoryNamespace);
                if (proof is null) { continue; }
                var sourceReference = SafeSourceReference.IsMatch(record.SourceReference)
                    ? record.SourceReference : "";
                candidates.Add((proof, new CustomerMemoryItem(
                    record.MemoryId,
                    record.Revision,
                    record.Text,
                    record.Kind.ToString(),
                    sourceReference,
                    record.CreatedAt,
                    proof.ProfileId,
                    proof.CustomerSessionId)));
            }
        }

        var ordered = candidates
            .OrderBy(candidate => candidate.Item.ProfileId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Item.CustomerSessionId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Item.Id, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Item.Revision)
            .DistinctBy(candidate => (candidate.Item.Id, candidate.Item.Revision))
            .ToArray();
        if (ordered.Length > CustomerDataLimits.MaxMemoryItems)
        {
            throw new CustomerDataAccessException(413, "memory_inventory_limit");
        }

        var items = Array.AsReadOnly(ordered.Select(candidate => candidate.Item).ToArray());
        var proofRows = ordered.Select(candidate => candidate.Proof).ToArray();
        var revision = Digest(new
        {
            InventoryRevision = scope.Inventory.Revision,
            Namespace = store.CustomerMemoryNamespace,
            Enabled = store.CustomerMemoryEnabled,
            LongTermPersonalEnabled = store.CustomerLongTermMemoryEnabled,
            GlobalEpoch = globalEpoch,
            GlobalDeletionEpoch = globalDeletionEpoch,
            Origins = origins,
            Records = proofRows,
            Items = items
        });

        scope.ValidateCurrent();
        return new CustomerMemoryDataSnapshot(scope.Inventory.Revision, revision, items, true);
    }

    private static bool SameSnapshot(CustomerMemoryDataSnapshot current, CustomerMemoryDataSnapshot expected) =>
        current.Complete && expected.Complete
        && current.InventoryRevision == expected.InventoryRevision
        && current.Revision == expected.Revision
        && current.Items.SequenceEqual(expected.Items);

    private static bool MatchesSha256(ReadOnlySpan<byte> payload, string expectedHex)
    {
        if (expectedHex.Length != 64) { return false; }
        byte[] expected;
        try { expected = Convert.FromHexString(expectedHex); }
        catch (FormatException) { return false; }
        if (expected.Length != SHA256.HashSizeInBytes) { return false; }
        var actual = SHA256.HashData(payload);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string Digest<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    private SqliteMemoryTransaction OpenMemoryDatabase()
    {
        var settings = new SqliteConnectionStringBuilder
        {
            DataSource = store.CustomerMemoryDatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 1
        };
        var connection = new SqliteConnection(settings.ToString());
        try
        {
            connection.Open();
            var transaction = connection.BeginTransaction(deferred: false);
            return new SqliteMemoryTransaction(connection, transaction);
        }
        catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6)
        {
            connection.Dispose();
            throw new CustomerDataAccessException(503, "customer_data_unavailable");
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private sealed record CustomerMemoryOriginState(
        CustomerOwnedSession Owned,
        string ExpectedGuestId,
        GuestMemoryPermission? Binding,
        long FactEpoch,
        long DeletionEpoch);

    private sealed class CustomerMemoryStoreGate(EveningMemoryStore store) : IDisposable
    {
        private EveningMemoryStore? heldStore = store;

        public void Dispose()
        {
            Interlocked.Exchange(ref heldStore, null)?.ExitCustomerMemoryGate();
        }
    }

    private sealed class SqliteMemoryTransaction(SqliteConnection connection, SqliteTransaction transaction) : IDisposable
    {
        internal SqliteConnection Connection { get; } = connection;
        internal SqliteTransaction Transaction { get; } = transaction;

        public void Dispose()
        {
            try { Transaction.Dispose(); }
            finally { Connection.Dispose(); }
        }
    }
    private sealed class CustomerMemoryGuard : ICustomerDataMemoryLease
    {
        private readonly CustomerMemoryData owner;
        private readonly HybridMemoryService service;
        private readonly CustomerDataGuardDeadline deadline;
        private readonly CancellationToken token;
        private IDisposable? serviceLease;

        internal CustomerMemoryGuard(CustomerMemoryData owner, HybridMemoryService service,
            CustomerDataGuardDeadline deadline, CancellationToken token, IDisposable serviceLease)
        {
            this.owner = owner;
            this.service = service;
            this.deadline = deadline;
            this.token = token;
            this.serviceLease = serviceLease;
        }

        internal bool BelongsTo(CustomerMemoryData expectedOwner, HybridMemoryService expectedService) =>
            ReferenceEquals(owner, expectedOwner) && ReferenceEquals(service, expectedService);

        public void ValidateCurrent()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref serviceLease) is null, this);
            token.ThrowIfCancellationRequested();
            deadline.Check();
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref serviceLease, null)?.Dispose();
        }
    }
}
