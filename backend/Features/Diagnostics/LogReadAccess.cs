using System.Security.Cryptography;
using System.Text;
using Kufr.Backend.Features.Access;

namespace Kufr.Backend.Features.Diagnostics;

internal sealed class LogReadAccess
{
    private readonly byte[]? hash;
    private readonly ServerAdminAccess admin;

    public LogReadAccess(IConfiguration configuration, ServerAdminAccess admin)
    {
        this.admin = admin;
        hash = ReadHash(configuration["Backend:Logs:CredentialSha256"]);
        if (hash is not null && (admin.MatchesHash(hash) || configuration.GetSection("Devices").GetChildren()
            .Any(device => ReadHash(device["CredentialSha256"]) is { } deviceHash
                && CryptographicOperations.FixedTimeEquals(hash, deviceHash))))
            throw new InvalidOperationException("Log reader credential must be separate from admin and devices.");
    }

    public bool Authenticate(HttpContext context)
    {
        if (admin.Authenticate(context)) return true;
        var header = context.Request.Headers.Authorization.ToString();
        return hash is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && header.Length is > 7 and <= 263
            && CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(Encoding.UTF8.GetBytes(header[7..])));
    }

    private static byte[]? ReadHash(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length != 64) throw new InvalidOperationException("Invalid log reader credential hash.");
        return Convert.FromHexString(value);
    }
}
