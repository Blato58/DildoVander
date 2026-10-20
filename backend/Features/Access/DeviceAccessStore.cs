using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Kufr.Backend.Features.Access;

internal sealed class DeviceAccessStore
{
    private readonly List<ProvisionedDevice> devices = [];
    private readonly ConcurrentDictionary<string, AccessGrant> grants = new(StringComparer.Ordinal);
    private readonly Lock grantGate = new();
    private readonly TimeProvider clock;
    private readonly ServerAdminAccess adminAccess;

    public DeviceAccessStore(IConfiguration configuration, TimeProvider clock)
    {
        this.clock = clock;
        adminAccess = new ServerAdminAccess(configuration);

        foreach (var section in configuration.GetSection("Devices").GetChildren())
        {
            var deviceId = section["DeviceId"];
            var credentialHash = section["CredentialSha256"];

            if (string.IsNullOrWhiteSpace(deviceId) || credentialHash is not { Length: 64 })
            {
                throw new InvalidOperationException("Invalid provisioned device configuration.");
            }

            byte[] hash;
            try
            {
                hash = Convert.FromHexString(credentialHash);
            }
            catch (FormatException exception)
            {
                throw new InvalidOperationException("Invalid provisioned device configuration.", exception);
            }

            if (devices.Any(device => device.DeviceId == deviceId))
            {
                throw new InvalidOperationException("Duplicate provisioned device identifier.");
            }

            if (devices.Any(device => CryptographicOperations.FixedTimeEquals(device.CredentialHash, hash)))
            {
                throw new InvalidOperationException("Duplicate provisioned device credential hash.");
            }

            if (deviceId == ServerAdminAccess.DebugDeviceId
                || adminAccess.MatchesHash(hash))
            {
                throw new InvalidOperationException("Server admin identity must be separate from devices.");
            }

            devices.Add(new ProvisionedDevice(deviceId, hash));
        }
    }

    public string? VerifyCredential(string credential)
    {
        if (adminAccess.Verify(credential))
        {
            return ServerAdminAccess.DebugDeviceId;
        }

        var candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(credential));
        string? deviceId = null;

        foreach (var device in devices)
        {
            if (CryptographicOperations.FixedTimeEquals(candidateHash, device.CredentialHash))
            {
                deviceId = device.DeviceId;
            }
        }

        return deviceId;
    }

    public IssuedToken? IssueToken(string deviceId)
    {
        lock (grantGate)
        {
            var now = clock.GetUtcNow();
            foreach (var grant in grants.Where(pair => pair.Value.ExpiresAt <= now).ToArray())
            {
                grants.TryRemove(grant.Key, out _);
            }

            // Bound memory if a provisioned device repeatedly exchanges its credential.
            if (grants.Values.Count(grant => grant.DeviceId == deviceId) >= 16)
            {
                return null;
            }

            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            grants[HashToken(token)] = new AccessGrant(deviceId, now.AddMinutes(15));
            return new IssuedToken(token, "Bearer", 900, deviceId);
        }
    }

    public string? VerifyAccessToken(string token)
    {
        var tokenHash = HashToken(token);
        if (!grants.TryGetValue(tokenHash, out var grant))
        {
            return null;
        }

        if (clock.GetUtcNow() >= grant.ExpiresAt)
        {
            grants.TryRemove(tokenHash, out _);
            return null;
        }

        return grant.DeviceId;
    }

    public void PurgeExpired()
    {
        var now = clock.GetUtcNow();
        foreach (var grant in grants.Where(pair => pair.Value.ExpiresAt <= now).ToArray())
        {
            grants.TryRemove(grant.Key, out _);
        }
    }

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private sealed record ProvisionedDevice(string DeviceId, byte[] CredentialHash);
    private sealed record AccessGrant(string DeviceId, DateTimeOffset ExpiresAt);
}

internal sealed record IssuedToken(string AccessToken, string TokenType, int ExpiresInSeconds, string DeviceId);
