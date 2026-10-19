using System.Security.Cryptography;
using System.Text;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.VoiceTurns;
using Microsoft.Extensions.Configuration;

internal static class ServerModeTests
{
    public static void Run()
    {
        ConversationOrchestrationTests.Run();
        var directory = Path.Combine(Path.GetTempPath(), $"kufr-modes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:DatabasePath"] = Path.Combine(directory, "journal.sqlite"),
                ["Backend:Admin:CredentialSha256"] = Hash("admin-secret"),
                ["Devices:0:DeviceId"] = "esp-test",
                ["Devices:0:CredentialSha256"] = Hash("device-secret")
            }).Build();
            var access = new DeviceAccessStore(configuration, TimeProvider.System);
            var admin = new ServerAdminAccess(configuration);
            Require(admin.Verify("admin-secret") && !admin.Verify("device-secret"), "separate admin access");
            Require(access.VerifyCredential("admin-secret") == ServerAdminAccess.DebugDeviceId, "debug identity");
            var token = access.IssueToken(ServerAdminAccess.DebugDeviceId)!;
            Require(!admin.Verify(token.AccessToken), "access tokens never grant admin");
            configuration["Devices:0:CredentialSha256"] = Hash("admin-secret");
            try
            {
                _ = new DeviceAccessStore(configuration, TimeProvider.System);
                throw new Exception("Device/admin credential collision accepted");
            }
            catch (InvalidOperationException) { }
            configuration["Devices:0:CredentialSha256"] = Hash("device-secret");
            configuration["Devices:0:DeviceId"] = ServerAdminAccess.DebugDeviceId;
            try
            {
                _ = new DeviceAccessStore(configuration, TimeProvider.System);
                throw new Exception("Reserved identity accepted");
            }
            catch (InvalidOperationException) { }

            using var journal = new OperationJournal(configuration, TimeProvider.System);
            var providerCalls = 0;
            using var dialog = new TextDialogRunner((_, _, _, _, _) =>
            {
                providerCalls++;
                throw new Exception("Mode control must never call provider");
            });
            using var voice = new VoiceProviderRunner(
                (_, _) => throw new Exception("Unexpected STT"),
                (_, _) => throw new Exception("Unexpected TTS"));
            dialog.SetEnabled(false);
            voice.SetEnabled(false);
            var store = new ConversationStore(TimeProvider.System, journal, dialog, voiceRunner: voice);
            var state = store.ReadServerModes();
            Require(state.Engines is { SchemaVersion: 1, PrimaryEngine: "gpt-live" }
                && state.Engines.Engines.Select(engine => engine.Engine).SequenceEqual(["gpt-live", "realtime"]),
                "only the two retained engines are advertised");
            Require(state.Engines!.Engines.All(engine => !engine.Ready && engine.UnavailableReason == "provider_credential_missing"),
                "readiness depends on provider configuration rather than legacy test runners");
            Require(store.ChangeServerModes(new(state.Revision, "responses", "openai")).Status == 410,
                "retired engine selection requires client upgrade");
            Require(store.ChangeServerModes(new("stale")).Status == 409, "stale state rejected");
            var enabled = store.ChangeServerModes(new(state.Revision));
            Require(enabled.Status == 200 && !dialog.Enabled && !voice.Enabled, "settings do not arm retired providers");
            Require(providerCalls == 0 && enabled.State!.Revision == state.Revision, "no-op retains revision without provider cost");
            var request = new CreateConversationRequest("esp-test", Guid.NewGuid(), 0, "catalog", "cs-CZ", new(false, false));
            var created = store.Create("esp-test", Guid.NewGuid(), "create"u8, request).Response!;
            var activeState = store.ReadServerModes();
            Require(activeState.ActiveConversations == 1, "active count");
            Require(store.ChangeServerModes(new(activeState.Revision, PersonalityId: PersonalityCatalog.OllivanderId)).Error == "conversations_active", "active conversation freezes persona");
            Require(store.ChangeServerModes(new(activeState.Revision)).Status == 200, "no-op allowed");
            store.EndConversation("esp-test", created.ConversationId, Guid.NewGuid());
            Require(store.ChangeServerModes(new(activeState.Revision, PersonalityId: PersonalityCatalog.OllivanderId)).Status == 200, "cleanup permits change");
            configuration["OPENAI_API_KEY"] = "fixture-key-never-dispatched";
            var unavailable = new ConversationStore(TimeProvider.System, journal, configuration: configuration);
            Require(unavailable.ReadServerModes().Engines!.Engines.All(engine => engine.Ready),
                "retained engines configure without legacy dialog or voice settings");
            Require(unavailable.ReadServerModes().Revision != state.Revision, "restart invalidates revision");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
        Console.WriteLine("Server mode tests passed.");
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }
}
