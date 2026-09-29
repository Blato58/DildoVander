using System.Security.Cryptography;

namespace Kufr.Backend.Features.Conversations;

internal sealed class ConversationStore(TimeProvider clock)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, ConversationEntry> conversations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IdempotencyEntry> idempotency = new(StringComparer.Ordinal);

    public CreateResult Create(string deviceId, Guid key, ReadOnlySpan<byte> requestBody)
    {
        var now = clock.GetUtcNow();
        var scopedKey = $"{deviceId}:POST:/v1/conversations:{key:D}";
        var bodyHash = Convert.ToHexString(SHA256.HashData(requestBody));

        lock (gate)
        {
            CleanupExpired(now);

            if (idempotency.TryGetValue(scopedKey, out var previous))
            {
                return previous.BodyHash == bodyHash
                    ? new CreateResult(201, previous.Response)
                    : new CreateResult(409, null);
            }

            if (conversations.Values.Any(entry => entry.DeviceId == deviceId))
            {
                return new CreateResult(409, null);
            }

            var response = new ConversationResponse(
                Guid.NewGuid().ToString("D"),
                now.AddMinutes(15),
                new ConversationLimits(10, 1000, 900, 1800));

            conversations.Add(response.ConversationId, new ConversationEntry(deviceId, response.ExpiresAt));
            idempotency.Add(scopedKey, new IdempotencyEntry(bodyHash, response, now.AddHours(24)));
            return new CreateResult(201, response);
        }
    }

    private void CleanupExpired(DateTimeOffset now)
    {
        foreach (var key in conversations.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
        {
            conversations.Remove(key);
        }

        foreach (var key in idempotency.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
        {
            idempotency.Remove(key);
        }
    }

    private sealed record ConversationEntry(string DeviceId, DateTimeOffset ExpiresAt);
    private sealed record IdempotencyEntry(string BodyHash, ConversationResponse Response, DateTimeOffset ExpiresAt);
}

internal sealed record CreateResult(int Status, ConversationResponse? Response);
internal sealed record ConversationResponse(string ConversationId, DateTimeOffset ExpiresAt, ConversationLimits Limits);
internal sealed record ConversationLimits(int MaxTurns, int MaxTextCharacters, int IdleSeconds, int MaxLifetimeSeconds);
internal sealed record ConversationConsent(bool Audio, bool Image);
internal sealed record CreateConversationRequest(
    string DeviceId,
    Guid BootId,
    int Generation,
    string CatalogVersion,
    string Locale,
    ConversationConsent Consent);
