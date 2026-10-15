using System.Security.Cryptography;
using System.Text;

namespace Kufr.Backend.Features.DigitalTwin;

internal sealed record TwinPrincipal(string Role, string? InstanceId, string? Mode);
internal sealed record TwinSession(string Id, TwinPrincipal Principal, DateTimeOffset Expires)
{
    private long expiryTicks = Expires.UtcTicks;
    public DateTimeOffset Expires
    {
        get => new(Interlocked.Read(ref expiryTicks), TimeSpan.Zero);
        init => Interlocked.Exchange(ref expiryTicks, value.UtcTicks);
    }
    // In-flight requests retain this authenticated session reference. Updating
    // its deadline prevents a delayed reservation from copying the old expiry.
    internal void Renew(DateTimeOffset expires) => Interlocked.Exchange(ref expiryTicks, expires.UtcTicks);
}

// These credentials are independent of device-control credentials and never
// grant access to conversations, inventory, manual leases or physical commands.
internal sealed class TwinAccess
{
    private readonly List<(byte[] Hash, TwinPrincipal Principal)> identities = [];
    private readonly Dictionary<string, TwinSession> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TwinSession> sourceTokens = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private readonly TimeProvider clock;
    public TwinAccess(IConfiguration configuration, TimeProvider clock)
    {
        this.clock = clock;
        foreach (var entry in configuration.GetSection("DigitalTwin:Credentials").GetChildren())
        {
            var role = entry["Role"];
            var instance = entry["InstanceId"];
            var mode = entry["Mode"];
            var hash = entry["Sha256"];
            if (role is not ("observe" or "control" or "ingest") || hash is not { Length: 64 }
                || role != "observe" && !TwinSnapshot.Id(instance)
                || role is "control" && mode != "simulation"
                || role is "ingest" && mode is not ("live" or "simulation"))
            {
                throw new InvalidOperationException("Invalid Digital Twin identity.");
            }
            var bytes = Convert.FromHexString(hash);
            if (identities.Any(identity => CryptographicOperations.FixedTimeEquals(bytes, identity.Hash)))
            {
                throw new InvalidOperationException("Digital Twin roles require distinct credentials.");
            }
            identities.Add((bytes, new TwinPrincipal(role!, instance, mode)));
        }
    }

    public TwinPrincipal? Verify(string? credential)
    {
        if (credential is not { Length: >= 32 and <= 256 }) { return null; }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(credential));
        TwinPrincipal? result = null;
        foreach (var entry in identities)
        {
            if (CryptographicOperations.FixedTimeEquals(hash, entry.Hash)) { result = entry.Principal; }
        }
        return result;
    }
    public TwinSession? IssueSourceToken(string deviceId, string instanceId)
    {
        lock (gate)
        {
            foreach (var key in sourceTokens.Where(entry => entry.Value.Expires <= clock.GetUtcNow()).Select(entry => entry.Key).ToArray())
            {
                sourceTokens.Remove(key);
            }
            if (sourceTokens.Count >= 256 || sourceTokens.Values.Count(value => value.Principal.InstanceId == instanceId) >= 16)
            {
                return null;
            }
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var grant = new TwinSession(token, new TwinPrincipal("ingest", instanceId, "live"), clock.GetUtcNow().AddMinutes(15));
            sourceTokens.Add(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))), grant with { Id = deviceId });
            return grant;
        }
    }
    public TwinPrincipal? VerifySource(string? token)
    {
        var configured = Verify(token);
        if (configured is { Role: "ingest" }) { return configured; }
        if (token is not { Length: 64 }) { return null; }
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        lock (gate)
        {
            return sourceTokens.TryGetValue(hash, out var grant) && grant.Expires > clock.GetUtcNow() ? grant.Principal : null;
        }
    }
    public TwinSession? CreateSession(string? credential, string? existingSessionId = null)
    {
        var principal = Verify(credential);
        if (principal is null || principal.Role == "ingest") { return null; }
        lock (gate)
        {
            foreach (var expiredId in sessions.Where(entry => entry.Value.Expires <= clock.GetUtcNow()).Select(entry => entry.Key).ToArray())
            {
                sessions.Remove(expiredId);
            }
            // Reauthenticating this browser must not orphan its command owner.
            // Another browser still receives a distinct session, and changing
            // credentials to another scope never reuses the previous authority.
            if (existingSessionId is not null && sessions.TryGetValue(existingSessionId, out var existing)
                && existing.Principal == principal)
            {
                existing.Renew(clock.GetUtcNow().AddHours(8));
                return existing;
            }
            if (sessions.Count >= 64) { return null; }
            var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var session = new TwinSession(id, principal, clock.GetUtcNow().AddHours(8));
            sessions.Add(id, session);
            return session;
        }
    }
    public TwinSession? Session(string? id)
    {
        lock (gate)
        {
            return id is not null && sessions.TryGetValue(id, out var session) && session.Expires > clock.GetUtcNow()
                ? session : null;
        }
    }
    public void Revoke(string? id)
    {
        if (id is null) { return; }
        lock (gate) { sessions.Remove(id); }
    }
}
