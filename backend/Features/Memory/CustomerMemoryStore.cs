using Microsoft.Data.Sqlite;
using System.Threading;
using Kufr.Backend.Features.Memory;

namespace Kufr.Backend.Features.Conversations;

internal sealed partial class EveningMemoryStore
{
    internal string CustomerMemoryNamespace => memoryOptions.Namespace;
    internal bool CustomerMemoryEnabled => memoryOptions.Enabled;
    internal bool CustomerLongTermMemoryEnabled => memoryOptions.LongTermPersonalEnabled;
    internal int CustomerMemoryMaxRecordBytes => memoryOptions.MaxRecordBytes;

    internal bool TryEnterCustomerMemoryGate(TimeSpan timeout) => gate.TryEnter(timeout);
    internal void ExitCustomerMemoryGate() => gate.Exit();

    internal long CustomerMemoryConversationEpoch(SqliteConnection connection, SqliteTransaction transaction,
        string device, string conversation, bool facts) =>
        ConversationEpoch(connection, transaction, device, conversation, facts);

    internal GuestMemoryPermission? CustomerMemoryReadGuest(SqliteConnection connection,
        SqliteTransaction transaction, string device, string conversation) =>
        ReadGuest(connection, transaction, device, conversation);

    internal static MemoryRecord CustomerMemoryReadRecord(SqliteDataReader reader) => ReadRecord(reader);

    internal static SqliteCommand CustomerMemoryCommand(SqliteConnection connection, SqliteTransaction transaction,
        string sql, params (string, object?)[] parameters)
    {
        var command = Command(connection, transaction, sql, parameters);
        command.CommandTimeout = 1;
        return command;
    }

    internal string CustomerMemoryDatabasePath
    {
        get
        {
            var builder = new SqliteConnectionStringBuilder(connectionString);
            if (builder.Mode != SqliteOpenMode.ReadWriteCreate
                || string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Customer memory requires a file-backed archive.");
            }

            return Path.GetFullPath(builder.DataSource);
        }
    }
}
