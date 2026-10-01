using System.Security.Cryptography;
using System.Text;

namespace Kufr.Backend.Features.Access;

internal sealed class ServerAdminAccess
{
    public const string DebugDeviceId = "debug-server-admin";
    private readonly byte[]? credentialHash;

    public ServerAdminAccess(IConfiguration configuration)
    {
        var hash = configuration["Backend:Admin:CredentialSha256"];
        if (!string.IsNullOrEmpty(hash))
        {
            if (hash.Length != 64)
            {
                throw new InvalidOperationException("Invalid server admin credential hash.");
            }

            credentialHash = Convert.FromHexString(hash);
        }
    }

    public bool Verify(string credential)
    {
        return credentialHash is not null
            && credential.Length is > 0 and <= 256
            && CryptographicOperations.FixedTimeEquals(
                credentialHash,
                SHA256.HashData(Encoding.UTF8.GetBytes(credential)));
    }

    public bool MatchesHash(byte[] hash) => credentialHash is not null
        && CryptographicOperations.FixedTimeEquals(credentialHash, hash);

    public bool Authenticate(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && Verify(header[7..]);
    }
}
