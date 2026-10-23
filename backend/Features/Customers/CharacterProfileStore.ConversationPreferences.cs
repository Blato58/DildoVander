using Kufr.Backend.Features.CustomerData;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Customers;

internal sealed partial class CharacterProfileStore
{
    private static void InitializeConversationPreferences(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS customer_conversation_preferences(
                account_id TEXT PRIMARY KEY, profile_id TEXT NOT NULL,
                revision INTEGER NOT NULL CHECK(typeof(revision)='integer' AND revision>0),
                language TEXT NOT NULL CHECK(language IN ('cs-CZ','en-US')),
                tone TEXT NOT NULL CHECK(tone IN ('gentle','cheeky','roast')));
            """;
        command.ExecuteNonQuery();
    }

    internal CustomerConversationPreferenceView? ReadHeldExportConversationPreferences(
        CustomerDataSqliteGuard guard, string accountId)
    {
        ValidateCustomerDataAccount(guard, accountId);
        var profile = Read(guard.Connection, guard.Transaction, accountId);
        if (profile is null || CustomerDataProfileRevoked(guard, accountId, profile.ProfileId)) { return null; }
        var value = ReadHeldConversationPreferences(guard, accountId, profile);
        return value.Persisted ? value : null;
    }

    internal CharacterProfile ReadHeldCurrentAdmissionProfile(CustomerDataSqliteGuard guard, string accountId)
    {
        ValidateCustomerDataAccount(guard, accountId);
        var profile = Read(guard.Connection, guard.Transaction, accountId);
        if (profile is null || CustomerDataProfileRevoked(guard, accountId, profile.ProfileId))
        { throw new CharacterException("character_not_found", 404); }
        return profile;
    }

    internal CustomerConversationPreferenceView ReadHeldConversationPreferences(CustomerDataSqliteGuard guard,
        string accountId, CharacterProfile profile)
    {
        using var command = guard.Command("""
            SELECT revision,language,tone FROM customer_conversation_preferences
            WHERE account_id=$account AND profile_id=$profile
            """);
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$profile", profile.ProfileId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new(1, profile.ProfileId, profile.Revision, 0,
                CustomerConversationPreferencePolicy.DefaultLanguage, CustomerConversationPreferencePolicy.DefaultTone, false);
        }
        var value = new CustomerConversationPreferenceSnapshot(1, reader.GetInt64(0), reader.GetString(1), reader.GetString(2));
        CustomerConversationPreferencePolicy.Validate(value);
        return new(1, profile.ProfileId, profile.Revision, value.PreferenceRevision, value.ConversationLanguage, value.Tone, true);
    }

    internal CustomerConversationPreferenceView WriteHeldConversationPreferences(CustomerDataSqliteGuard guard,
        string accountId, CharacterProfile profile, CustomerConversationPreferenceWrite request)
    {
        var current = ReadHeldConversationPreferences(guard, accountId, profile);
        if (request.ExpectedPreferenceRevision != current.PreferenceRevision || current.PreferenceRevision == long.MaxValue)
        { throw new CharacterException("conversation_preferences_revision_conflict", 409); }
        var revision = current.PreferenceRevision + 1;
        using var command = guard.Command("""
            INSERT INTO customer_conversation_preferences(account_id,profile_id,revision,language,tone)
            VALUES($account,$profile,$revision,$language,$tone)
            ON CONFLICT(account_id) DO UPDATE SET profile_id=$profile,revision=$revision,language=$language,tone=$tone
            """);
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$profile", profile.ProfileId);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$language", request.ConversationLanguage);
        command.Parameters.AddWithValue("$tone", request.Tone);
        command.ExecuteNonQuery();
        return new(1, profile.ProfileId, profile.Revision, revision, request.ConversationLanguage, request.Tone, true);
    }
}
