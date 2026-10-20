using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kufr.Backend.Features.Access;

namespace Kufr.Backend.Features.Firmware;

// Public, read-only distribution. The signature is the release authority;
// device credentials and provider secrets are never used by this endpoint.
internal sealed class FirmwareReleases(IConfiguration configuration)
{
    internal const int SlotSize = 0x640000;
    private readonly string? directory = configuration["Backend:Firmware:Directory"];

    public byte[]? ReadManifest(string channel)
    {
        if (channel is not ("stable" or "development") || directory is null)
        {
            return null;
        }

        var bytes = ReadFile(Path.Combine("manifests", channel + ".json"), 8192);
        var trust = ReadFile("trust-" + channel + ".pem", 2048);
        if (bytes is null || trust is null)
        {
            return null;
        }

        try
        {
            using var envelope = JsonDocument.Parse(bytes);
            var root = envelope.RootElement;
            if (!ExactFields(root, "schemaVersion", "payload", "signature")
                || root.GetProperty("schemaVersion").GetInt32() != 1)
            {
                return null;
            }

            var payload = Convert.FromBase64String(root.GetProperty("payload").GetString()!);
            var signature = Convert.FromBase64String(root.GetProperty("signature").GetString()!);
            if (payload.Length is < 1 or > 4096 || signature.Length is < 8 or > 80)
            {
                return null;
            }

            using var key = ECDsa.Create();
            key.ImportFromPem(System.Text.Encoding.ASCII.GetString(trust));
            if (key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7"
                || !key.VerifyData(payload, signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence))
            {
                return null;
            }

            using var metadata = JsonDocument.Parse(payload);
            var manifest = metadata.RootElement;
            if (!ExactFields(manifest, "schemaVersion", "version", "buildId", "hardware", "profile",
                    "channel", "updaterVersion", "minUpdaterVersion", "url", "sizeBytes", "sha256", "signatureAlgorithm")
                || manifest.GetProperty("schemaVersion").GetInt32() != 1
                || manifest.GetProperty("hardware").GetString() != "esp32s3-n16r8"
                || manifest.GetProperty("profile").GetString() != "B-effects"
                || manifest.GetProperty("channel").GetString() != channel
                || manifest.GetProperty("signatureAlgorithm").GetString() != "ecdsa-p256-sha256"
                || manifest.GetProperty("updaterVersion").GetInt32() != 1
                || manifest.GetProperty("minUpdaterVersion").GetInt32() != 1
                || !Regex.IsMatch(manifest.GetProperty("version").GetString() ?? "", @"^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$", RegexOptions.CultureInvariant)
                || !Regex.IsMatch(manifest.GetProperty("buildId").GetString() ?? "", "^[a-f0-9]{40}$", RegexOptions.CultureInvariant))
            {
                return null;
            }

            var hash = manifest.GetProperty("sha256").GetString() ?? "";
            var size = manifest.GetProperty("sizeBytes").GetInt32();
            if (!ValidHash(hash) || size is <= 0 or > SlotSize
                || manifest.GetProperty("url").GetString() != $"/v1/firmware/artifacts/{hash}.bin")
            {
                return null;
            }

            // Never advertise a missing or partially published artifact.
            var artifact = ReadArtifact(hash);
            return artifact?.Length == size ? bytes : null;
        }
        catch (Exception exception) when (exception is JsonException or FormatException
            or InvalidOperationException or KeyNotFoundException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    public byte[]? ReadArtifact(string hash)
    {
        if (!ValidHash(hash))
        {
            return null;
        }

        var bytes = ReadFile(Path.Combine("artifacts", hash + ".bin"), SlotSize);
        return bytes is not null && Convert.ToHexStringLower(SHA256.HashData(bytes)) == hash ? bytes : null;
    }

    private byte[]? ReadFile(string relativePath, int maximumSize)
    {
        if (directory is null || !Path.IsPathFullyQualified(directory))
        {
            return null;
        }

        try
        {
            var path = Path.Combine(directory, relativePath);
            // The configured root must be administrator-owned. Reject links at
            // the root, subdirectory and file so a release cannot escape it.
            foreach (var entry in new[] { directory, Path.GetDirectoryName(path)!, path })
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                {
                    return null;
                }
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 || stream.Length > maximumSize)
            {
                return null;
            }

            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool ValidHash(string value) =>
        Regex.IsMatch(value, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant);

    private static bool ExactFields(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var names = element.EnumerateObject().Select(property => property.Name).ToArray();
        return names.Length == expected.Length && names.Distinct(StringComparer.Ordinal).Count() == names.Length
            && names.All(name => expected.Contains(name, StringComparer.Ordinal));
    }
}

internal static class FirmwareReleaseEndpoints
{
    public static IEndpointRouteBuilder MapFirmwareReleases(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/firmware/manifests/{channel}.json", (string channel, HttpContext context, FirmwareReleases releases) =>
        {
            var bytes = releases.ReadManifest(channel);
            return bytes is null ? ApiProblem.Create(context, 404, "firmware_release_unavailable")
                : Results.Bytes(bytes, "application/json");
        }).RequireRateLimiting("firmware");
        endpoints.MapGet("/v1/firmware/artifacts/{hash}.bin", (string hash, HttpContext context, FirmwareReleases releases) =>
        {
            var bytes = releases.ReadArtifact(hash);
            return bytes is null ? ApiProblem.Create(context, 404, "firmware_artifact_unavailable")
                : Results.Bytes(bytes, "application/octet-stream");
        }).RequireRateLimiting("firmware");
        return endpoints;
    }
}
