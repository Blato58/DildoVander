using Kufr.Backend.Features.Customers;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Conversations;

internal sealed record CustomerWaitingEveningScope(string? EveningId, bool Open, long? ActivationRevision);

internal sealed partial class EveningMemoryStore
{
    private static void MigrateCustomerWaitingActivation(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('evenings') WHERE name='activation_revision'";
        if ((long)command.ExecuteScalar()! != 0) { return; }
        // Zero marks a legacy active row. Only a new explicit activation can
        // establish a usable revision; startup and Ensure must not invent one.
        command.CommandText = """
            ALTER TABLE evenings ADD COLUMN activation_revision INTEGER NOT NULL DEFAULT 0
                CHECK(typeof(activation_revision)='integer' AND activation_revision>=0);
            """;
        command.ExecuteNonQuery();
    }

    private static bool TryNextWaitingActivation(SqliteConnection connection, SqliteTransaction transaction,
        string device, out long next)
    {
        next = 0;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT activation_revision,typeof(activation_revision) FROM evenings WHERE device_id=$device";
        command.Parameters.AddWithValue("$device", device);
        using var row = command.ExecuteReader();
        if (!row.Read() || row.GetString(1) != "integer") { return false; }
        var current = row.GetInt64(0);
        if (current < 0 || current == long.MaxValue) { return false; }
        // Bind the checked Int64 result. SQLite's numeric promotion at MAX+1
        // must never turn the durable counter into an imprecise REAL value.
        next = checked(current + 1);
        return true;
    }

    internal CustomerWaitingEveningScope ReadCustomerWaitingEvening(string device,
        CancellationToken token = default)
        => WithWaitingMemoryScope(device, token, (connection, transaction) =>
            ReadWaitingEveningScope(connection, transaction, device));

    internal CustomerWaitingEvent WithCustomerWaitingEvent(string device, string eveningId, long? activationRevision,
        Func<CustomerWaitingEvent> journalAction, CancellationToken token = default)
        => WithValidatedWaitingScope(device, eveningId, activationRevision, journalAction, token);

    internal CustomerWaitingView WithCustomerWaitingEvening(string device, string eveningId, long? activationRevision,
        Func<CustomerWaitingView> journalAction, CancellationToken token = default)
        => WithValidatedWaitingScope(device, eveningId, activationRevision, journalAction, token);

    internal CustomerTicketRecord WithCustomerWaitingTicket(string device, string eveningId, long? activationRevision,
        Func<CustomerTicketRecord> journalAction, CancellationToken token = default)
        => WithValidatedWaitingScope(device, eveningId, activationRevision, journalAction, token);

    internal CustomerSessionRecord WithCustomerWaitingSession(string device, string eveningId, long? activationRevision,
        Func<CustomerSessionRecord> journalAction, CancellationToken token = default)
        => WithValidatedWaitingScope(device, eveningId, activationRevision, journalAction, token);

    private T WithValidatedWaitingScope<T>(string device, string eveningId, long? activationRevision,
        Func<T> journalAction, CancellationToken token)
        => WithWaitingMemoryScope(device, token, (connection, transaction) =>
        {
            RequireWaitingEveningScope(connection, transaction, device, eveningId, activationRevision);
            token.ThrowIfCancellationRequested();
            return journalAction();
        }, reserveActivation: true);

    // Concrete synchronous return types above prevent an async callback from
    // escaping with this thread-affine gate held. The only nested writer is the
    // journal, preserving existing Show admission's memory -> journal order.
    private T WithWaitingMemoryScope<T>(string device, CancellationToken token,
        Func<SqliteConnection, SqliteTransaction, T> action, bool reserveActivation = false)
    {
        if (!ValidShowIdentifier(device)) { throw new CustomerSessionException(400, "invalid_waiting_device"); }
        token.ThrowIfCancellationRequested();
        if (!gate.TryEnter(100)) { throw new CustomerSessionException(503, "waiting_temporarily_unavailable"); }
        try
        {
            token.ThrowIfCancellationRequested();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder(connectionString)
            {
                Mode = reserveActivation ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
                DefaultTimeout = 1
            }.ToString());
            connection.Open();
            // The immediate writer reservation also excludes activations from
            // another store instance/process, including in WAL mode. Availability
            // reads alone need no writer reservation.
            using var transaction = connection.BeginTransaction(deferred: !reserveActivation);
            token.ThrowIfCancellationRequested();
            // No memory writes or distributed-commit promise. A committed
            // journal command remains replayable if its HTTP response is lost.
            return action(connection, transaction);
        }
        catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6)
        {
            throw new CustomerSessionException(503, "waiting_temporarily_unavailable");
        }
        finally { gate.Exit(); }
    }

    private CustomerWaitingEveningScope ReadWaitingEveningScope(SqliteConnection connection,
        SqliteTransaction transaction, string device)
    {
        string evening;
        long? activation;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT evening_id,activation_revision,typeof(activation_revision) FROM evenings WHERE device_id=$device";
            command.Parameters.AddWithValue("$device", device);
            using var row = command.ExecuteReader();
            if (!row.Read()) { return new(null, false, null); }
            evening = row.GetString(0);
            activation = row.GetString(2) == "integer" && row.GetInt64(1) > 0 ? row.GetInt64(1) : null;
        }
        return new(evening, activation is not null
            && !ShowClosed(connection, transaction, device, evening)
            && !ShowMemoryScopeDeleted(connection, transaction, device, evening), activation);
    }

    private void RequireWaitingEveningScope(SqliteConnection connection,
        SqliteTransaction transaction, string device, string expected, long? expectedActivation)
    {
        if (!Guid.TryParseExact(expected, "D", out _)) { throw new CustomerSessionException(400, "invalid_waiting_evening"); }
        if (expectedActivation is null or <= 0) { throw new CustomerSessionException(409, "waiting_activation_required"); }
        var scope = ReadWaitingEveningScope(connection, transaction, device);
        if (scope.EveningId != expected) { throw new CustomerSessionException(409, "waiting_evening_changed"); }
        if (scope.ActivationRevision != expectedActivation) { throw new CustomerSessionException(409, "waiting_activation_changed"); }
        if (!scope.Open) { throw new CustomerSessionException(409, "waiting_evening_closed"); }
    }
}
