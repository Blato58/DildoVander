using System.Security.Cryptography;
using System.Text;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Microsoft.Extensions.Configuration;

var clock = new AdjustableTimeProvider(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
var conversations = new ConversationStore(clock);
var bootId = Guid.NewGuid();
var request = new CreateConversationRequest("device-a", bootId, 1, "catalog-1", "cs-CZ", new ConversationConsent(false, false));
var body = Encoding.UTF8.GetBytes("create-body");
var key = Guid.NewGuid();
var created = conversations.Create("device-a", key, body, request);
Require(created.Status == 201 && created.Response is not null, "conversation creation");
var conversationId = created.Response!.ConversationId;

var turn = new TextTurnRequest(Guid.NewGuid(), bootId, 1, "private text", 63, 7, "catalog-1");
var turnBody = Encoding.UTF8.GetBytes("turn-body");
var turnKey = Guid.NewGuid();
var accepted = conversations.SubmitTurn("device-a", conversationId, turnKey, turnBody, turn);
Require(accepted.Status == 202 && accepted.OperationId is not null, "fake turn acceptance");

clock.Advance(TimeSpan.FromMinutes(15));
var replay = conversations.SubmitTurn("device-a", conversationId, turnKey, turnBody, turn);
Require(replay.OperationId == accepted.OperationId, "idempotent retry after idle expiry");
var replacement = conversations.Create("device-a", Guid.NewGuid(), body, request);
Require(replacement.Status == 201, "new conversation after idle expiry");
Require(conversations.Create("device-a", key, body, request).Response?.ConversationId == conversationId,
    "create retry retained for 24 hours");

var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
var configuration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Devices:0:DeviceId"] = "device-a",
        ["Devices:0:CredentialSha256"] = hash
    })
    .Build();
var access = new DeviceAccessStore(configuration, clock);
Require(access.VerifyCredential(secret) == "device-a", "provisioned credential");
var issued = access.IssueToken("device-a");
Require(issued is not null && access.VerifyAccessToken(issued.AccessToken) == "device-a", "issued token");
clock.Advance(TimeSpan.FromMinutes(15));
Require(access.VerifyAccessToken(issued!.AccessToken) is null, "token expiry");

var duplicateConfiguration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Devices:0:DeviceId"] = "device-a",
        ["Devices:0:CredentialSha256"] = hash,
        ["Devices:1:DeviceId"] = "device-b",
        ["Devices:1:CredentialSha256"] = hash
    })
    .Build();
try
{
    _ = new DeviceAccessStore(duplicateConfiguration, clock);
    throw new Exception("Duplicate credential hash was accepted.");
}
catch (InvalidOperationException)
{
    // A shared credential must not select a device by configuration order.
}

Console.WriteLine("Backend clock tests: session, retry, token expiry and provisioning passed");

static void Require(bool condition, string name)
{
    if (!condition)
    {
        throw new Exception($"Failed: {name}");
    }
}

sealed class AdjustableTimeProvider(DateTimeOffset current) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => current;

    public void Advance(TimeSpan duration)
    {
        current += duration;
    }
}
