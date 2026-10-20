using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.Memory;
using Kufr.Backend.Features.Realtime;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

internal static class OperationEpochTests
{
    private const string Maximum = "18446744073709551615";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    internal static async Task RunAsync()
    {
        VerifyWireIdentity();
        var root = Path.Combine(Path.GetTempPath(), "backend-epoch-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await VerifyEndpointsAsync(root);
            await VerifyLabOutcomesAsync(root);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
        Console.WriteLine("PASS canonical epoch wire identities, strict request parsing and authenticated outcome echo.");
    }

    private static void VerifyWireIdentity()
    {
        foreach (var value in new string?[] { null, "0", "1", "9007199254740993", Maximum })
        {
            Require(OperationEpoch.IsValid(value), "canonical uint64 epoch accepted");
            Require(OperationEpoch.Key(value) == (value ?? "0"), "exact identity retained");
        }
        Require(OperationEpoch.Same(null, "0") && !OperationEpoch.Same(null, "1"), "absent means only legacy zero");
        foreach (var value in new[] { "", "00", "01", "+1", "-1", "1.0", "1e1", " 1", "1 ", "1\n", "18446744073709551616" })
        {
            Require(!OperationEpoch.IsValid(value), "noncanonical or overflowing epoch rejected");
        }
        var request = new RealtimeTurnRequest(Guid.NewGuid(), Guid.NewGuid(), 1, 63, 7,
            PartyWandCatalog.Version, true, "", null, "marin", 1);
        var legacy = JsonSerializer.Serialize(request, RealtimeProtocol.JsonOptions);
        Require(!legacy.Contains("operationEpoch", StringComparison.Ordinal), "legacy request shape retained");
        Require(RealtimeProtocol.ReadRequest(Encoding.UTF8.GetBytes(legacy)) is { OperationEpoch: null }, "legacy parse retained");
        foreach (var value in new[] { "0", "9007199254740993", Maximum })
        {
            var body = JsonSerializer.Serialize(request with { OperationEpoch = value }, RealtimeProtocol.JsonOptions);
            var parsed = RealtimeProtocol.ReadRequest(Encoding.UTF8.GetBytes(body));
            Require(parsed?.OperationEpoch == value && RealtimeProtocol.IsValid(parsed), "scoped request preserves every digit");
        }
        foreach (var raw in new[] { "null", "1", "\"01\"", "\"18446744073709551616\"" })
        {
            var body = legacy[..^1] + ",\"operationEpoch\":" + raw + "}";
            Require(RealtimeProtocol.ReadRequest(Encoding.UTF8.GetBytes(body)) is null, "present invalid epoch rejected");
        }
        var duplicate = legacy[..^1] + ",\"operationEpoch\":\"1\",\"operationEpoch\":\"1\"}";
        Require(RealtimeProtocol.ReadRequest(Encoding.UTF8.GetBytes(duplicate)) is null, "duplicate epoch rejected");
        var correlation = new TurnCorrelation("epoch-device", request.BootId, Guid.NewGuid().ToString("D"), request.ClientTurnId, 1, 7);
        Require(!JsonSerializer.Serialize(correlation, Web).Contains("operationEpoch", StringComparison.Ordinal), "legacy correlation shape retained");
        foreach (var raw in new[] { "{\"operationEpoch\":null}", "{\"physicalFingerprint\":1}",
            "{\"OperationEpoch\":null}", "{\"OperationEpoch\":\"1\"}",
            "{\"operationEpoch\":\"1\",\"OperationEpoch\":\"2\"}",
            "{\"PhysicalFingerprint\":\"1\"}", "{\"operationEpoch\":\"1\",\"operationEpoch\":\"2\"}" })
        {
            using var document = JsonDocument.Parse(raw);
            Require(!KeepsakeOutcomeEndpoints.HasValidIdentityFields(document.RootElement), "invalid optional proof field rejected");
        }
    }

    private static async Task VerifyEndpointsAsync(string root)
    {
        const string device = "epoch-device";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = Path.Combine(root, "journal.sqlite"),
            ["Keepsake:Enabled"] = "true", ["Keepsake:DataRoot"] = Path.Combine(root, "content"),
            ["Backend:Memory:Enabled"] = "false", ["Backend:Memory:LongTermPersonalEnabled"] = "false"
        }).Build();
        using var services = new ServiceCollection()
            .AddSingleton(_ => new KeepsakeStore(config, TimeProvider.System))
            .BuildServiceProvider();
        using var journal = new OperationJournal(config, TimeProvider.System);
        var access = new DeviceAccessStore(config, TimeProvider.System);
        var token = access.IssueToken(device)!.AccessToken;
        var conversations = new ConversationStore(TimeProvider.System, journal);
        var create = new CreateConversationRequest(device, Guid.NewGuid(), 1, PartyWandCatalog.Version, "cs-CZ", new(false, false));
        var legacyBody = JsonSerializer.Serialize(create, Web);
        foreach (var raw in new[] { "null", "1", "\"01\"", "\"18446744073709551616\"" })
        {
            var context = Context(legacyBody[..^1] + ",\"operationEpoch\":" + raw + "}");
            Require(Status(await ConversationEndpoints.CreateConversationAsync(context, access, conversations)) == 400, "create rejects invalid epoch before reservation");
        }
        var created = await ConversationEndpoints.CreateConversationAsync(
            Context(JsonSerializer.Serialize(create with { OperationEpoch = Maximum }, Web)), access, conversations);
        Require(Status(created) == 201, "authenticated scoped creation succeeds");

        var memory = new EveningMemoryStore(config, journal);
        var options = HybridMemoryOptions.Read(config, journal);
        using var provider = new OpenAiVectorMemoryProvider(config, options);
        var hybrid = new HybridMemoryService(memory, provider, options, TimeProvider.System);
        var proposal = new SelectionProposal(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            Guid.NewGuid(), PartyWandCatalog.Version, 7, 2, "", create.BootId, 1, DateTimeOffset.UtcNow.AddMinutes(1), Maximum);
        var correlation = new TurnCorrelation(device, proposal.BootId, proposal.ConversationId, proposal.TurnId, 1, 7, Maximum);
        var operation = journal.Reserve(device, "/epoch-proof", Guid.NewGuid(), "body", correlation);
        Require(journal.MarkSelectionCompleted(operation.OperationId, proposal), "selection evidence committed");
        var outcome = new KeepsakeOutcomeEndpoints.OutcomeRequest(1, Guid.NewGuid().ToString("D"), proposal.ProposalId,
            proposal.ProposalId, proposal.BootId.ToString("D"), 1, 7, "completed_by_timing", Maximum, Maximum);
        foreach (var wrong in new[]
        {
            outcome with { OperationEpoch = null }, outcome with { OperationEpoch = "1" },
            outcome with { PhysicalFingerprint = null }, outcome with { PhysicalFingerprint = "0" },
            outcome with { PhysicalOperationId = Guid.NewGuid().ToString("D") }
        })
        {
            Require(Status(await PublishAsync(wrong)) == 409, "scoped proof mismatch cannot issue or acknowledge content");
        }
        var accepted = await PublishAsync(outcome);
        Require(Status(accepted) == 201, "exact scoped physical outcome accepted");
        var acknowledgement = (accepted as IValueHttpResult)?.Value as KeepsakeOutcomeEndpoints.OutcomeAcknowledgement;
        Require(acknowledgement is { OperationEpoch: Maximum, PhysicalFingerprint: Maximum }, "ACK echoes original identity exactly");
        Require(Status(await PublishAsync(outcome)) == 201, "same original proof replay is idempotent");
        Require(Status(await PublishAsync(outcome with { PhysicalFingerprint = "1" })) == 409, "changed fingerprint conflicts with durable confirmation");

        Task<IResult> PublishAsync(KeepsakeOutcomeEndpoints.OutcomeRequest value) =>
            KeepsakeOutcomeEndpoints.PublishAsync(Context(JsonSerializer.Serialize(value, Web)), operation.OperationId,
                access, journal, config, memory, hybrid);
        DefaultHttpContext Context(string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Headers.Authorization = "Bearer " + token;
            context.Request.Headers["Idempotency-Key"] = Guid.NewGuid().ToString("D");
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = bytes.Length;
            return context;
        }
    }

    private static async Task VerifyLabOutcomesAsync(string root)
    {
        const string device = "epoch-lab-device";
        var path = Path.Combine(root, "lab.sqlite");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = path, ["Keepsake:Enabled"] = "false",
            ["Backend:Memory:Enabled"] = "false", ["Backend:Memory:LongTermPersonalEnabled"] = "false"
        }).Build();
        // A technical Lab ACK must not resolve or construct production archive storage.
        using var services = new ServiceCollection().BuildServiceProvider();
        var access = new DeviceAccessStore(config, TimeProvider.System);
        var token = access.IssueToken(device)!.AccessToken;
        foreach (var epoch in new string?[] { null, Maximum })
        {
            SelectionProposal proposal;
            string operationId;
            using (var journal = new OperationJournal(config, TimeProvider.System))
            {
                var created = journal.ReserveCreate(device, Guid.NewGuid(), "create", "lab-session", "lab-revision",
                    DateTimeOffset.UtcNow.AddSeconds(180), "lab-owner");
                proposal = new(Guid.NewGuid().ToString("D"), created.Response!.ConversationId, Guid.NewGuid(),
                    PartyWandCatalog.Version, 7, 2, "", Guid.NewGuid(), 1, DateTimeOffset.UtcNow.AddMinutes(1), epoch);
                var correlation = new TurnCorrelation(device, proposal.BootId, proposal.ConversationId,
                    proposal.TurnId, proposal.Generation, proposal.DeviceRevision, epoch);
                operationId = journal.Reserve(device, "/lab-proof", Guid.NewGuid(), "body", correlation).OperationId;
                Require(!journal.MarkSelectionCompleted(operationId, proposal with { OperationEpoch = "1" }),
                    "Lab proof cannot change the reserved epoch");
                Require(journal.ReadLabSelection(device, operationId)?.Proposal is null,
                    "rejected Lab proof leaves no durable selection");
                Require(journal.MarkSelectionCompleted(operationId, proposal), "exact Lab epoch commits without production archive");
            }
            // Restart must retain both original epoch and durable test origin.
            using var reopened = new OperationJournal(config, TimeProvider.System);
            Require(reopened.ReadLabSelection(device, operationId)?.Proposal?.OperationEpoch == epoch,
                "Lab proof retains original epoch after reopen");
            var memory = new EveningMemoryStore(config, reopened);
            var options = HybridMemoryOptions.Read(config, reopened);
            using var provider = new OpenAiVectorMemoryProvider(config, options);
            var hybrid = new HybridMemoryService(memory, provider, options, TimeProvider.System);
            var outcome = new KeepsakeOutcomeEndpoints.OutcomeRequest(1, Guid.NewGuid().ToString("D"), proposal.ProposalId,
                proposal.ProposalId, proposal.BootId.ToString("D"), 1, 7, "completed_by_timing", epoch,
                epoch is null ? null : Maximum);
            if (epoch is not null)
            {
                foreach (var wrong in new[] { outcome with { OperationEpoch = null }, outcome with { OperationEpoch = "1" },
                    outcome with { PhysicalFingerprint = null }, outcome with { PhysicalFingerprint = "0" },
                    outcome with { PhysicalOperationId = Guid.NewGuid().ToString("D") } })
                {
                    Require(Status(await PublishAsync(wrong)) == 409, "Lab scoped identity mismatch cannot be acknowledged");
                }
            }
            var accepted = await PublishAsync(outcome);
            var acknowledgement = (accepted as IValueHttpResult)?.Value as KeepsakeOutcomeEndpoints.LabOutcomeAcknowledgement;
            Require(Status(accepted) == 201 && acknowledgement?.OperationEpoch == epoch
                && acknowledgement?.PhysicalFingerprint == outcome.PhysicalFingerprint, "Lab ACK echoes exact original scoped identity");
            Require(Status(await PublishAsync(outcome)) == 201, "Lab proof replay is idempotent after restart");
            Require(Status(await PublishAsync(outcome with { PhysicalFingerprint = "1" })) == 409,
                "Lab confirmation rejects changed durable proof");
            var json = JsonSerializer.Serialize(acknowledgement, Web);
            Require(!json.Contains("locator", StringComparison.Ordinal), "Lab ACK cannot expose production locator");
            if (epoch is null)
            {
                using var legacy = JsonDocument.Parse(json);
                Require(legacy.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).SequenceEqual(
                    new[] { "outcomeId", "physicalOperationId", "schemaVersion", "sessionEpoch" }), "legacy Lab ACK shape retained");
            }
            Require(reopened.ReadKeepsakeSelection(device, operationId) is null, "Lab outcome never enters production keepsake evidence");
            using var inspect = new SqliteConnection("Data Source=" + path);
            inspect.Open();
            using var query = inspect.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='keepsake_selection_outbox'";
            Require(Convert.ToInt64(query.ExecuteScalar()) == 0, "Lab-only journal never creates production outbox");

            Task<IResult> PublishAsync(KeepsakeOutcomeEndpoints.OutcomeRequest value)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Web);
                var context = new DefaultHttpContext { RequestServices = services };
                context.Request.Headers.Authorization = "Bearer " + token;
                context.Request.Headers["Idempotency-Key"] = Guid.NewGuid().ToString("D");
                context.Request.Body = new MemoryStream(bytes);
                context.Request.ContentType = "application/json";
                context.Request.ContentLength = bytes.Length;
                return KeepsakeOutcomeEndpoints.PublishAsync(context, operationId, access, reopened, config, memory, hybrid);
            }
        }
    }

    private static int? Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode;
    private static void Require(bool value, string message)
    {
        if (!value) { throw new InvalidOperationException(message); }
    }
}
