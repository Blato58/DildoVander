using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.Memory;
using Kufr.Backend.Features.Show;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class MemoryShowDeletionTests
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "show-scope-deletion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await VerifyAsync(root); }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private static async Task VerifyAsync(string root)
    {
        var database = Path.Combine(root, "journal.sqlite");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = database, ["Keepsake:DataRoot"] = Path.Combine(root, "content"),
            ["Keepsake:Enabled"] = "true", ["Backend:Memory:Enabled"] = "true",
            ["Backend:Memory:CloudIndexingEnabled"] = "false",
            ["Backend:Admin:CredentialSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("scope-test-admin")))
        }).Build();
        using var journal = new OperationJournal(config, TimeProvider.System);
        var memory = new EveningMemoryStore(config, journal);
        var options = HybridMemoryOptions.Read(config, journal);
        using var provider = new OpenAiVectorMemoryProvider(config, options);
        var hybrid = new HybridMemoryService(memory, provider, options, TimeProvider.System);
        var conversations = new ConversationStore(TimeProvider.System, journal, eveningMemory: memory, configuration: config);
        var keepsakes = new KeepsakeStore(config, TimeProvider.System, memoryStore: memory);
        var access = new DeviceAccessStore(config, TimeProvider.System);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddConfiguration(config);
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(journal);
        builder.Services.AddSingleton(memory);
        builder.Services.AddSingleton(hybrid);
        builder.Services.AddSingleton(conversations);
        builder.Services.AddSingleton(keepsakes);
        builder.Services.AddSingleton(access);
        builder.Services.AddSingleton<ServerAdminAccess>();
        await using var app = builder.Build();
        app.MapMemoryAdministration();
        Delegate physicalOutcomeHandler = KeepsakeOutcomeEndpoints.PublishAsync;
        app.MapPost("/v1/keepsakes/outcomes/{operationId:guid}", physicalOutcomeHandler);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        client.DefaultRequestHeaders.Authorization = new("Bearer", "scope-test-admin");
        var victim = Guid.NewGuid().ToString("D");
        var survivor = Guid.NewGuid().ToString("D");
        var evening = memory.CaptureShowVisit("device-a", victim)!;
        memory.CaptureShowVisit("device-a", survivor);
        var physicalId = Guid.NewGuid().ToString("D");
        Check(memory.RecordConfirmedShowOutcome("device-a", victim, evening, physicalId, 1, "completed_by_timing"), "victim physical fixture");
        memory.RecordConfirmedShowOutcome("device-a", survivor, evening, Guid.NewGuid().ToString("D"), 2, "completed_by_timing");
        memory.RecordShowEvent(new("identity-fixture", ShowMemoryEventKind.IdentityCompleted, "device-a", evening,
            "keepsake-fixture", 1, "fixture", ConversationId: victim));
        memory.SetGuestPermission("device-a", survivor, "host-042", "operator_verified", true, true, false, 0);
        var foreignEvening = memory.CaptureShowVisit("device-b", victim)!;
        memory.RecordConfirmedShowOutcome("device-b", victim, foreignEvening, Guid.NewGuid().ToString("D"), 1, "completed_by_timing");
        Check(memory.BuildEveningLore("device-a", evening).Motifs.Any(item => item.Id == "wand_room_1"), "active lore derives from documented physical result");

        async Task DeleteAsync(string device, string scope, string subject)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", "scope-test-admin");
            using var request = new HttpRequestMessage(HttpMethod.Delete, "/v1/admin/memory/scopes/" + scope + "/" + Uri.EscapeDataString(subject))
            { Content = JsonContent.Create(new { deviceId = device, expectedEpoch = memory.MemoryEpoch }) };
            using var response = await client.SendAsync(request);
            Check(response.StatusCode == HttpStatusCode.OK, "actual canonical admin scope deletion succeeds");
        }
        await DeleteAsync("device-a", "conversation", victim);
        Check(memory.ReadChronicle("device-a", evening) is { CompletedVisits: 1, ConfirmedPresentations: 1 }, "conversation deletion removes associated show facts only");
        Check(memory.BuildEveningLore("device-a", evening).Motifs.All(item => item.Id != "wand_room_1"), "deleted source cannot survive as active evening lore");
        Check(memory.ReadChronicle("device-b", foreignEvening)!.ConfirmedPresentations == 1, "same conversation label on another device remains isolated");
        Check(Sql("SELECT count(*) FROM show_memory_events WHERE device_id='device-a' AND conversation_id='" + victim + "' AND revoked=0") == 0,
            "show facts revoked under canonical deletion transaction");
        Check(!memory.RecordConfirmedShowOutcome("device-a", victim, evening, Guid.NewGuid().ToString("D"), 1, "completed_by_timing"), "unseen late outcome rejected by conversation deletion fence");

        // The real outcome endpoint must reject even a durable legacy proposal,
        // rather than bootstrapping deleted conversation memory on retry.
        var boot = Guid.NewGuid();
        var correlation = new TurnCorrelation("device-a", boot, victim, Guid.NewGuid(), 0, 1);
        var proposal = new SelectionProposal(Guid.NewGuid().ToString("D"), victim, correlation.ClientTurnId,
            PartyWandCatalog.Version, 1, 1, "", boot, 0, DateTimeOffset.UtcNow.AddMinutes(1));
        var operation = journal.Reserve("device-a", "/fixture-outcome", Guid.NewGuid(), "fixture", correlation);
        journal.MarkSelectionCompleted(operation.OperationId, proposal, evening);
        client.DefaultRequestHeaders.Authorization = new("Bearer", access.IssueToken("device-a")!.AccessToken);
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/v1/keepsakes/outcomes/" + operation.OperationId)
        {
            Content = JsonContent.Create(new KeepsakeOutcomeEndpoints.OutcomeRequest(1, Guid.NewGuid().ToString("D"), proposal.ProposalId,
                Guid.NewGuid().ToString("D"), boot.ToString("D"), 0, 1, "completed_by_timing"))
        })
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
            using var response = await client.SendAsync(request);
            Check(response.StatusCode == HttpStatusCode.Conflict, "actual late outcome endpoint cannot revive canonically deleted conversation");
        }
        var archived = memory.EndShowEvening("device-a", evening)!;
        Check(archived.ConfirmedPresentations == 1, "archive fixture retains surviving source");
        await DeleteAsync("device-a", "guest", "host-042");
        var redacted = memory.ReadChronicle("device-a", evening)!;
        Check(redacted.ConfirmedPresentations == 0 && redacted.CompletedVisits == 0, "guest deletion rebuilds historical chronicle without bound conversations");
        Check(Sql("SELECT count(*) FROM show_chronicle_editions WHERE device_id='device-a' AND evening_id='" + evening + "' AND revision<" + redacted.Revision + " AND withdrawn=0") == 0,
            "old editions containing deleted facts are all withdrawn");

        var otherEvening = memory.GetStatus("evening-only").EveningId;
        var offlineOperation = Guid.NewGuid().ToString("D");
        memory.RecordImportedOfflineOutcome("evening-only", otherEvening, offlineOperation, 3, "device_report");
        memory.RecordShowEvent(new("duel-only", ShowMemoryEventKind.DuelCompleted, "evening-only", otherEvening, "duel-only", 1, "fixture"));
        Check(memory.BuildEveningLore("evening-only", otherEvening).Motifs.Count > 0, "show-only evening has documented lore without raw transcripts");
        await DeleteAsync("evening-only", "evening", otherEvening);
        Check(memory.ReadChronicle("evening-only", otherEvening) is { CompletedVisits: 0, ConfirmedPresentations: 0, CompletedDuels: 0 }
            && memory.BuildEveningLore("evening-only", otherEvening).Motifs.Count == 0, "evening deletion removes all show-only sources and lore");
        Check(!memory.RecordImportedOfflineOutcome("evening-only", otherEvening, Guid.NewGuid().ToString("D"), 3, "device_report")
            && !memory.RecordShowEvent(new("late-duel", ShowMemoryEventKind.DuelCompleted, "evening-only", otherEvening, "new-duel", 1, "fixture"))
            && memory.CaptureShowVisit("evening-only", Guid.NewGuid().ToString("D")) is null,
            "canonical evening tombstone fences previously unseen imported and online show sources");

        var legacyConversation = Guid.NewGuid().ToString("D");
        var legacyEvening = memory.CaptureShowVisit("legacy-clear", legacyConversation)!;
        memory.RecordConfirmedShowOutcome("legacy-clear", legacyConversation, legacyEvening,
            Guid.NewGuid().ToString("D"), 4, "completed_by_timing");
        memory.EndShowEvening("legacy-clear", legacyEvening);
        conversations.ClearEveningMemory("legacy-clear", legacyEvening);
        var replacementEvening = memory.ReadActiveEveningId("legacy-clear")!;
        Check(replacementEvening != legacyEvening && memory.ReadChronicle("legacy-clear", legacyEvening) is null,
            "legacy clear preserves its new-evening switch and removes old public catalog access");
        Check(Sql("SELECT count(*) FROM show_memory_events WHERE device_id='legacy-clear' AND evening_id='" + legacyEvening + "' AND revoked=0") == 0
            && Sql("SELECT count(*) FROM show_chronicle_editions WHERE device_id='legacy-clear' AND evening_id='" + legacyEvening
                + "' AND withdrawn=0 AND json_extract(snapshot,'$.confirmedPresentations')>0") == 0,
            "legacy clear atomically revokes show sources and all historical editions containing their facts");
        Check(!memory.RecordConfirmedShowOutcome("legacy-clear", legacyConversation, legacyEvening,
                Guid.NewGuid().ToString("D"), 4, "completed_by_timing")
            && !memory.RecordImportedOfflineOutcome("legacy-clear", legacyEvening,
                Guid.NewGuid().ToString("D"), 4, "device_report"),
            "legacy clear tombstone prevents both late online and imported physical restoration");
        var nextConversation = Guid.NewGuid().ToString("D");
        Check(memory.CaptureShowVisit("legacy-clear", nextConversation) == replacementEvening,
            "legacy clear still admits independent visits to the replacement evening");
        conversations.ClearEveningMemory("legacy-clear", legacyEvening);
        Check(memory.ReadActiveEveningId("legacy-clear") == replacementEvening
            && memory.ReadCapturedShowEvening("legacy-clear", nextConversation) == replacementEvening,
            "legacy clear retry cannot remove the replacement evening or its new visit");
        await app.StopAsync();
        Console.WriteLine("Canonical memory/show deletion HTTP tests passed (conversation/guest/evening, archived editions, lore, late outcomes).");

        long Sql(string sql)
        {
            using var connection = new SqliteConnection("Data Source=" + database + ".evening.sqlite");
            connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }
    }
    private static void Check(bool value, string message)
    {
        if (!value) { throw new InvalidOperationException("Canonical memory/show deletion: " + message); }
    }
}
