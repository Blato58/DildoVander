using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.CustomerData;

// One acquisition budget spans every database and the memory writer admission.
internal sealed class CustomerDataGuardDeadline(CancellationToken token)
{
    private readonly long started = Stopwatch.GetTimestamp();

    internal void Check()
    {
        token.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(5))
        {
            throw new CustomerDataAccessException(503, "customer_data_unavailable");
        }
    }
}

// Dedicated connections avoid borrowing EF's tracked context or a feature writer.
// BEGIN IMMEDIATE excludes concurrent revocation writes until the caller releases
// the bounded operation. Admission/export readers roll back on disposal. The
// dedicated preference mutation commits its character scope after final
// authority validation and immediately closes both reservations.
internal sealed class CustomerDataSqliteGuard : IDisposable
{
    internal SqliteConnection Connection { get; }
    internal SqliteTransaction Transaction { get; }

    private CustomerDataSqliteGuard(SqliteConnection connection, SqliteTransaction transaction)
    {
        Connection = connection;
        Transaction = transaction;
    }

    internal static CustomerDataSqliteGuard Acquire(string connectionString, CustomerDataGuardDeadline deadline)
    {
        deadline.Check();
        var settings = new SqliteConnectionStringBuilder(connectionString) { DefaultTimeout = 1 };
        var connection = new SqliteConnection(settings.ToString());
        SqliteTransaction? transaction = null;
        try
        {
            connection.Open();
            deadline.Check();
            transaction = connection.BeginTransaction(deferred: false);
            deadline.Check();
            return new CustomerDataSqliteGuard(connection, transaction);
        }
        catch (Exception error)
        {
            transaction?.Dispose();
            connection.Dispose();
            if (error is SqliteException { SqliteErrorCode: 5 or 6 })
            {
                throw new CustomerDataAccessException(503, "customer_data_unavailable");
            }
            throw;
        }
    }

    internal SqliteCommand Command(string sql)
    {
        var command = Connection.CreateCommand();
        command.Transaction = Transaction;
        command.CommandTimeout = 1;
        command.CommandText = sql;
        return command;
    }

    public void Dispose()
    {
        try { Transaction.Dispose(); }
        finally { Connection.Dispose(); }
    }
}
