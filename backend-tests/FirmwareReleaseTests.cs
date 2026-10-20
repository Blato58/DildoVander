using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Firmware;
using Microsoft.Extensions.Configuration;

internal static class FirmwareReleaseTests
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kufr-ota-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "manifests"));
        Directory.CreateDirectory(Path.Combine(directory, "artifacts"));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:Firmware:Directory"] = directory
            }).Build();
            var releases = new FirmwareReleases(configuration);
            var artifact = Encoding.UTF8.GetBytes("nonproduction test image");
            var hash = Convert.ToHexStringLower(SHA256.HashData(artifact));
            var artifactPath = Path.Combine(directory, "artifacts", hash + ".bin");
            File.WriteAllBytes(artifactPath, artifact);
            File.WriteAllText(Path.Combine(directory, "trust-development.pem"), key.ExportSubjectPublicKeyInfoPem());
            var metadata = new
            {
                schemaVersion = 1, version = "1.2.3", buildId = new string('a', 40), hardware = "esp32s3-n16r8",
                profile = "B-effects", channel = "development", updaterVersion = 1, minUpdaterVersion = 1,
                url = $"/v1/firmware/artifacts/{hash}.bin", sizeBytes = artifact.Length, sha256 = hash,
                signatureAlgorithm = "ecdsa-p256-sha256"
            };
            var payload = JsonSerializer.SerializeToUtf8Bytes(metadata);
            var signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            var envelope = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, payload = Convert.ToBase64String(payload), signature = Convert.ToBase64String(signature)
            });
            var manifestPath = Path.Combine(directory, "manifests", "development.json");
            File.WriteAllBytes(manifestPath, envelope);
            Require(releases.ReadManifest("development")?.SequenceEqual(envelope) == true, "signed release accepted");
            Require(releases.ReadArtifact(hash)?.SequenceEqual(artifact) == true, "artifact served exactly");
            Require(releases.ReadManifest("stable") is null, "no fabricated stable release");
            Require(releases.ReadArtifact("../trust") is null && releases.ReadManifest("../trust") is null, "traversal rejected");
            File.WriteAllBytes(artifactPath, Encoding.UTF8.GetBytes("corrupted"));
            Require(releases.ReadArtifact(hash) is null && releases.ReadManifest("development") is null, "corruption never advertised");
            File.WriteAllBytes(artifactPath, artifact);
            using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            File.WriteAllText(Path.Combine(directory, "trust-development.pem"), otherKey.ExportSubjectPublicKeyInfoPem());
            Require(releases.ReadManifest("development") is null, "wrong authority rejected");
            File.WriteAllText(Path.Combine(directory, "trust-development.pem"), key.ExportSubjectPublicKeyInfoPem());
            var duplicate = Encoding.UTF8.GetString(envelope).Replace("{", "{\"schemaVersion\":1,", StringComparison.Ordinal);
            File.WriteAllText(manifestPath, duplicate);
            Require(releases.ReadManifest("development") is null, "duplicate envelope fields rejected");
            File.WriteAllBytes(manifestPath, envelope);
            var tampered = Encoding.UTF8.GetString(envelope).Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal);
            File.WriteAllText(manifestPath, tampered);
            Require(releases.ReadManifest("development") is null, "unsupported schema rejected");
            configuration["Backend:Firmware:Directory"] = "relative";
            Require(new FirmwareReleases(configuration).ReadManifest("development") is null, "relative roots disabled");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
        Console.WriteLine("Firmware release tests passed.");
    }

    private static void Require(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }
}
