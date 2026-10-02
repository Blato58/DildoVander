using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Usage;

internal sealed record CostLimits(long SessionMicroUsd, long DailyMicroUsd, long MonthlyMicroUsd)
{
    public const long DefaultSessionMicroUsd = 3_000_000;
    public bool Enabled => SessionMicroUsd > 0;

    public static CostLimits Read(IConfiguration configuration)
    {
        var raw = configuration["Backend:Usage:SessionCapMicroUsd"];
        if (string.IsNullOrWhiteSpace(raw)) return new(DefaultSessionMicroUsd, 0, 0);
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var cap)
            || cap <= 0 || cap > DefaultSessionMicroUsd)
            throw new InvalidOperationException("Session cap must be positive and at most 3 USD.");
        // Daily/monthly totals remain auditable; the OpenAI project owns the
        // global spending limit. Legacy period-cap settings are ignored.
        return new(cap, 0, 0);
    }

}

internal static class CostReservations
{
    public static bool TryReserve(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CostLimits limits,
        DateTimeOffset now,
        string operationId,
        string deviceId,
        string conversationId,
        long estimatedMicroUsd)
    {
        if (estimatedMicroUsd == 0)
        {
            return true;
        }

        if (estimatedMicroUsd < 0 || !limits.Enabled)
        {
            return false;
        }

        var day = now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var month = now.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var sessionUsage = ReadUsage(
            connection,
            transaction,
            "SELECT charged_micro_usd FROM cost_session_usage WHERE conversation_id = $key",
            conversationId);
        if (Exceeds(sessionUsage, estimatedMicroUsd, limits.SessionMicroUsd))
        {
            return false;
        }

        using (var reservation = connection.CreateCommand())
        {
            reservation.Transaction = transaction;
            reservation.CommandText = """
                INSERT INTO cost_reservations
                    (operation_id, device_id, conversation_id, day_utc, month_utc,
                     reserved_micro_usd, status, created_at_ms)
                VALUES ($operation, $device, $conversation, $day, $month,
                        $amount, 'reserved', $now)
                """;
            reservation.Parameters.AddWithValue("$operation", operationId);
            reservation.Parameters.AddWithValue("$device", deviceId);
            reservation.Parameters.AddWithValue("$conversation", conversationId);
            reservation.Parameters.AddWithValue("$day", day);
            reservation.Parameters.AddWithValue("$month", month);
            reservation.Parameters.AddWithValue("$amount", estimatedMicroUsd);
            reservation.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            reservation.ExecuteNonQuery();
        }

        AddUsage(connection, transaction, conversationId, deviceId, day, month, estimatedMicroUsd, now);
        return true;
    }

    public static bool Settle(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationId,
        long actualMicroUsd,
        DateTimeOffset now)
    {
        if (actualMicroUsd < 0)
        {
            return false;
        }

        string deviceId;
        string conversationId;
        string day;
        string month;
        long reserved;
        using (var reservation = connection.CreateCommand())
        {
            reservation.Transaction = transaction;
            reservation.CommandText = """
                SELECT device_id, conversation_id, day_utc, month_utc,
                       reserved_micro_usd, actual_micro_usd, status
                FROM cost_reservations WHERE operation_id = $operation
                """;
            reservation.Parameters.AddWithValue("$operation", operationId);
            using var reader = reservation.ExecuteReader();
            if (!reader.Read())
            {
                return false;
            }

            if (reader.GetString(6) == "settled")
            {
                return !reader.IsDBNull(5) && reader.GetInt64(5) == actualMicroUsd;
            }

            deviceId = reader.GetString(0);
            conversationId = reader.GetString(1);
            day = reader.GetString(2);
            month = reader.GetString(3);
            reserved = reader.GetInt64(4);
        }

        long difference;
        try
        {
            difference = checked(actualMicroUsd - reserved);
        }
        catch (OverflowException)
        {
            return false;
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE cost_reservations
                SET actual_micro_usd = $actual, status = 'settled'
                WHERE operation_id = $operation AND status = 'reserved'
                """;
            update.Parameters.AddWithValue("$actual", actualMicroUsd);
            update.Parameters.AddWithValue("$operation", operationId);
            if (update.ExecuteNonQuery() != 1)
            {
                return false;
            }
        }

        AddUsage(connection, transaction, conversationId, deviceId, day, month, difference, now);
        return true;
    }

    public static void Purge(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM cost_reservations WHERE created_at_ms <= $auditCutoff;
            DELETE FROM cost_session_usage WHERE updated_at_ms <= $auditCutoff;
            DELETE FROM cost_period_usage WHERE updated_at_ms <= $aggregateCutoff;
            """;
        command.Parameters.AddWithValue("$auditCutoff", now.AddDays(-7).ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$aggregateCutoff", now.AddDays(-90).ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    private static bool Exceeds(long current, long estimate, long cap) => estimate > cap - current;

    private static long ReadUsage(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() is long amount ? amount : 0;
    }

    private static void AddUsage(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string conversationId,
        string deviceId,
        string day,
        string month,
        long amount,
        DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = amount < 0 ? """
            UPDATE cost_session_usage
            SET charged_micro_usd = charged_micro_usd + $amount, updated_at_ms = $now
            WHERE conversation_id = $conversation AND device_id = $device;
            UPDATE cost_period_usage
            SET charged_micro_usd = charged_micro_usd + $amount, updated_at_ms = $now
            WHERE period_kind = 'day' AND period_key = $day;
            UPDATE cost_period_usage
            SET charged_micro_usd = charged_micro_usd + $amount, updated_at_ms = $now
            WHERE period_kind = 'month' AND period_key = $month;
            """ : """
            INSERT INTO cost_session_usage (conversation_id, device_id, charged_micro_usd, updated_at_ms)
            VALUES ($conversation, $device, $amount, $now)
            ON CONFLICT(conversation_id) DO UPDATE SET
                charged_micro_usd = charged_micro_usd + excluded.charged_micro_usd,
                updated_at_ms = excluded.updated_at_ms;
            INSERT INTO cost_period_usage
                (period_kind, period_key, charged_micro_usd, updated_at_ms)
            VALUES ('day', $day, $amount, $now)
            ON CONFLICT(period_kind, period_key) DO UPDATE SET
                charged_micro_usd = charged_micro_usd + excluded.charged_micro_usd,
                updated_at_ms = excluded.updated_at_ms;
            INSERT INTO cost_period_usage
                (period_kind, period_key, charged_micro_usd, updated_at_ms)
            VALUES ('month', $month, $amount, $now)
            ON CONFLICT(period_kind, period_key) DO UPDATE SET
                charged_micro_usd = charged_micro_usd + excluded.charged_micro_usd,
                updated_at_ms = excluded.updated_at_ms;
            """;
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$day", day);
        command.Parameters.AddWithValue("$month", month);
        command.Parameters.AddWithValue("$amount", amount);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }
}
