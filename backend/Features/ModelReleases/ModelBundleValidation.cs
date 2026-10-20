using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kufr.Backend.Features.ModelReleases;

internal sealed record ModelAsset(string Path, string Kind, string Sha256, long Bytes);
internal sealed record ModelRelease(string BundleHash, string ModelId, string Revision, string SourceRevision,
    string CadVerification, bool CanSelect, long Bytes);
internal sealed class ModelReleaseException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

internal static class ModelBundleValidation
{
    public const long MaxBytes = 96L * 1024 * 1024;
    public static bool IsHash(string? value) => value is not null && Regex.IsMatch(value, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    private static bool IsId(string value) => Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9_.:-]{0,159}$", RegexOptions.CultureInvariant);
    private static bool IsRouteId(string value) => Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9_.:+-]{0,159}\z", RegexOptions.CultureInvariant);
    private static bool IsSourceEvidencePath(string value) => value.Length is > 0 and <= 240
        && Regex.IsMatch(value, @"\A[\p{L}\p{N}_-]+(?:[.][\p{L}\p{N}_-]+)*(?:/[\p{L}\p{N}_-]+(?:[.][\p{L}\p{N}_-]+)*)*\z", RegexOptions.CultureInvariant);
    private static bool IsAssetPath(string value) => value.Length is > 0 and <= 240
        && Regex.IsMatch(value, "^[A-Za-z0-9_-]+(?:[.][A-Za-z0-9_-]+)*(?:/[A-Za-z0-9_-]+(?:[.][A-Za-z0-9_-]+)*)*$", RegexOptions.CultureInvariant);
    internal static void Require(bool condition)
    {
        if (!condition) { throw new ModelReleaseException("model_release_invalid"); }
    }
    internal static string Text(JsonElement value, string name) => value.GetProperty(name).GetString() ?? throw new ModelReleaseException("model_release_invalid");
    private static void Fields(JsonElement value, params string[] fields)
    {
        Require(value.ValueKind == JsonValueKind.Object);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) { Require(fields.Contains(property.Name) && seen.Add(property.Name)); }
        Require(seen.Count == fields.Length);
    }
    internal static void CheckRealPath(string root, string relative, bool directory = false)
    {
        var path = root;
        foreach (var segment in relative.Split('/'))
        {
            Require(segment.Length > 0 && segment is not "." and not "..");
            path = System.IO.Path.Combine(path, segment);
            var attributes = File.GetAttributes(path);
            Require((attributes & FileAttributes.ReparsePoint) == 0);
        }
        Require(directory ? Directory.Exists(path) : File.Exists(path));
    }
    internal static byte[] CanonicalManifest(JsonElement manifest)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            WriteCanonical(writer, manifest, true);
        }
        return output.ToArray();
    }
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value, bool root = false)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    Require(seen.Add(property.Name));
                    if (root && property.Name == "bundleHash") { continue; }
                    writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) { WriteCanonical(writer, item); } writer.WriteEndArray(); break;
            case JsonValueKind.String: writer.WriteStringValue(value.GetString()); break;
            case JsonValueKind.Number: writer.WriteNumberValue(value.GetInt64()); break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            case JsonValueKind.Null: writer.WriteNullValue(); break;
            default: throw new ModelReleaseException("model_release_invalid");
        }
    }
    public static async Task<ModelRelease> ReadAsync(string directory, string hash, bool verifyContents, CancellationToken cancellationToken)
    {
        Require(IsHash(hash));
        Require((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0);
        CheckRealPath(directory, "model-bundle.json");
        var manifestPath = System.IO.Path.Combine(directory, "model-bundle.json");
        Require(new FileInfo(manifestPath).Length is > 0 and <= 128 * 1024);
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath, cancellationToken));
        var manifest = document.RootElement;
        Fields(manifest, "schemaVersion", "modelId", "revision", "bundleHash", "units", "coordinates", "compatibility", "provenance", "assets");
        Require(manifest.GetProperty("schemaVersion").GetInt32() == 1 && Text(manifest, "bundleHash") == hash);
        Require(Convert.ToHexStringLower(SHA256.HashData(CanonicalManifest(manifest))) == hash);
        var id = Text(manifest, "modelId"); var revision = Text(manifest, "revision");
        Require(IsId(id) && IsId(revision) && Text(manifest, "units") == "mm" && Text(manifest, "coordinates") == "right-handed-z-up");
        var compatibility = manifest.GetProperty("compatibility"); Fields(compatibility, "viewerSchema");
        Require(compatibility.GetProperty("viewerSchema").GetInt32() == 1);
        var provenance = manifest.GetProperty("provenance"); Fields(provenance, "kind", "sourceRevision", "cadVerification");
        var kind = Text(provenance, "kind"); var source = Text(provenance, "sourceRevision"); var verification = Text(provenance, "cadVerification");
        Require(kind is "cad-export" or "synthetic-fixture" && verification is "verified" or "not-verified");
        Require(source.Length is > 0 and <= 160 && source.All(c => c is >= ' ' and <= '~'));
        Require(kind != "synthetic-fixture" || verification == "not-verified");
        var assets = manifest.GetProperty("assets"); Require(assets.ValueKind == JsonValueKind.Array && assets.GetArrayLength() is > 0 and <= 32);
        var parsed = new List<ModelAsset>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var asset in assets.EnumerateArray())
        {
            Fields(asset, "path", "kind", "sha256", "bytes");
            var path = Text(asset, "path"); var assetKind = Text(asset, "kind"); var digest = Text(asset, "sha256"); var bytes = asset.GetProperty("bytes").GetInt64();
            Require(IsAssetPath(path));
            Require(names.Add(path) && IsHash(digest) && bytes is > 0 and <= MaxBytes && assetKind is "model-json" or "bom-json" or "verification-json");
            total += bytes; Require(total <= MaxBytes);
            CheckRealPath(directory, path);
            Require(new FileInfo(System.IO.Path.Combine(directory, path)).Length == bytes);
            parsed.Add(new(path, assetKind, digest, bytes));
        }
        Require(parsed.Count(x => x.Kind == "model-json") == 1);
        Require(parsed.Count(x => x.Kind == "verification-json") == (verification == "verified" ? 1 : 0));
        var inventory = new HashSet<string>(StringComparer.Ordinal) { "model-bundle.json" };
        foreach (var asset in parsed) { inventory.Add(asset.Path); }
        CheckInventory(directory, directory, inventory);
        if (verifyContents)
        {
            foreach (var asset in parsed)
            {
                var assetPath = System.IO.Path.Combine(directory, asset.Path);
                await using (var stream = File.OpenRead(assetPath))
                {
                    Require(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)) == asset.Sha256);
                }
                using var data = JsonDocument.Parse(await File.ReadAllBytesAsync(assetPath, cancellationToken));
                if (asset.Kind == "model-json") { ValidateModel(data.RootElement, id, revision); }
                else if (asset.Kind == "verification-json")
                {
                    ValidateVerification(data.RootElement, id, source, parsed.Single(x => x.Kind == "model-json").Sha256);
                }
                else { Require(data.RootElement.ValueKind is JsonValueKind.Array or JsonValueKind.Object); }
            }
        }
        return new(hash, id, revision, source, verification, kind == "cad-export" && verification == "verified", total);
    }
    private static void ValidateVerification(JsonElement report, string modelId, string sourceRevision, string modelHash)
    {
        Require(Text(report, "result") == "PASS_CAD_NOT_PHYSICAL_ACCEPTANCE"
            && report.GetProperty("physical_test_performed").ValueKind == JsonValueKind.False);
        var evidence = report.GetProperty("modelEvidence");
        Require(evidence.GetProperty("schemaVersion").GetInt32() == 1 && Text(evidence, "modelId") == modelId
            && Text(evidence, "modelAssetSha256") == modelHash && Text(evidence, "sourceRevision") == sourceRevision);
        var sourceHashes = evidence.GetProperty("sourceHashes"); var recorded = report.GetProperty("sources");
        Require(sourceHashes.ValueKind == JsonValueKind.Object && recorded.ValueKind == JsonValueKind.Object);
        var count = 0; var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sourceHashes.EnumerateObject())
        {
            count++; Require(count <= 512 && paths.Add(source.Name) && IsSourceEvidencePath(source.Name)
                && source.Value.ValueKind == JsonValueKind.String && IsHash(source.Value.GetString())
                && recorded.TryGetProperty(source.Name, out var actual) && actual.ValueKind == JsonValueKind.String && actual.GetString() == source.Value.GetString());
        }
        Require(count > 0);
    }
    private static void CheckInventory(string root, string directory, HashSet<string> files)
    {
        var entries = Directory.EnumerateFileSystemEntries(directory).Take(128).ToArray(); Require(entries.Length < 128);
        foreach (var entry in entries)
        {
            Require((File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0);
            var relative = System.IO.Path.GetRelativePath(root, entry).Replace('\\', '/');
            if (Directory.Exists(entry))
            {
                Require(files.Any(x => x.StartsWith(relative + "/", StringComparison.Ordinal))); CheckInventory(root, entry, files);
            }
            else { Require(files.Contains(relative)); }
        }
    }
    private static double[] Vector(JsonElement value, int size)
    {
        Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == size);
        var result = value.EnumerateArray().Select(x => x.GetDouble()).ToArray(); Require(result.All(double.IsFinite)); return result;
    }
    private static void ValidateModel(JsonElement model, string id, string revision)
    {
        Require(Text(model, "modelId") == id && Text(model, "revision") == revision);
        var meshes = model.GetProperty("meshes"); Require(meshes.ValueKind == JsonValueKind.Array && meshes.GetArrayLength() is > 0 and <= 10000);
        var ids = new HashSet<string>(StringComparer.Ordinal); var names = new HashSet<string>(StringComparer.Ordinal); var parents = new Dictionary<string, string?>();
        double[] identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
        foreach (var mesh in meshes.EnumerateArray())
        {
            var meshId = Text(mesh, "id"); var name = Text(mesh, "name");
            Require(IsId(meshId) && IsId(name) && ids.Add(meshId) && names.Add(name));
            var group = Text(mesh, "group"); Require(group.Length is > 0 and <= 100 && group.All(c => c >= ' '));
            Require(Vector(mesh.GetProperty("color"), 3).All(x => x is >= 0 and <= 1));
            var bounds = Vector(mesh.GetProperty("bounds"), 6); Require(Enumerable.Range(0, 3).All(i => bounds[i] <= bounds[i + 3]));
            Require(Vector(mesh.GetProperty("transform"), 16).SequenceEqual(identity));
            var parent = mesh.GetProperty("parentId"); Require(parent.ValueKind == JsonValueKind.Null);
            parents[meshId] = null;
            var vertices = mesh.GetProperty("vertices"); var triangles = mesh.GetProperty("triangles");
            Require(vertices.ValueKind == JsonValueKind.Array && triangles.ValueKind == JsonValueKind.Array);
            Require(vertices.GetArrayLength() is > 0 and <= 9000000 && vertices.GetArrayLength() % 3 == 0);
            Require(triangles.GetArrayLength() is > 0 and <= 9000000 && triangles.GetArrayLength() % 3 == 0);
            Require(vertices.EnumerateArray().All(x => x.TryGetDouble(out var n) && double.IsFinite(n)));
            Require(triangles.EnumerateArray().All(x => x.TryGetInt32(out var n) && n >= 0 && n < vertices.GetArrayLength() / 3));
            var vertexIndex = 0;
            foreach (var vertex in vertices.EnumerateArray())
            {
                var axis = vertexIndex++ % 3; var number = vertex.GetDouble();
                Require(number >= bounds[axis] - .01 && number <= bounds[axis + 3] + .01);
            }
        }
        foreach (var part in parents)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { part.Key }; var parent = part.Value;
            while (parent is not null) { Require(parents.ContainsKey(parent) && seen.Add(parent)); parent = parents[parent]; }
        }
        Vector(model.GetProperty("hinge"), 3);
        var mechanism = model.GetProperty("mechanism");
        foreach (var key in new[] { "origin_y", "origin_z", "cam_y", "cam_z", "dwell_degrees", "travel_degrees", "lift" }) { Require(double.IsFinite(mechanism.GetProperty(key).GetDouble())); }
        Require(mechanism.GetProperty("travel_degrees").GetDouble() > mechanism.GetProperty("dwell_degrees").GetDouble() * 2 && mechanism.GetProperty("dwell_degrees").GetDouble() >= 0 && mechanism.GetProperty("lift").GetDouble() >= 0 && mechanism.GetProperty("count").GetInt32() is >= 1 and <= 6);
        var steps = model.GetProperty("assemblyGuide").GetProperty("steps"); Require(steps.ValueKind == JsonValueKind.Array && steps.GetArrayLength() <= 500);
        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps.EnumerateArray())
        {
            var stepId = Text(step, "id"); var title = Text(step, "title");
            Require(IsId(stepId) && stepIds.Add(stepId) && title.Length is > 0 and <= 1000 && title.All(c => c >= ' '));
            foreach (var field in new[] { "parts", "caseParts", "benchParts", "preparedParts" })
            {
                if (!step.TryGetProperty(field, out var values)) { continue; }
                Require(values.ValueKind == JsonValueKind.Array && values.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String && Regex.IsMatch(x.GetString()!, "^[A-Za-z0-9_*.-]{1,160}$")));
            }
        }
        foreach (var key in new[] { "routes", "sources", "connections", "bomReferences" }) { var array = model.GetProperty(key); Require(array.ValueKind == JsonValueKind.Array && array.GetArrayLength() <= 20000); }
        ValidateRoutes(model.GetProperty("routes"));
        Vector(model.GetProperty("presentation").GetProperty("target"), 3);
        var mapping = model.GetProperty("twinManifest");
        if (mapping.ValueKind != JsonValueKind.Null) { ValidateTelemetryMapping(mapping, names); }
    }
    private static void ValidatePolyline(JsonElement points)
    {
        Require(points.ValueKind == JsonValueKind.Array && points.GetArrayLength() is >= 2 and <= 10000);
        foreach (var point in points.EnumerateArray()) { Vector(point, 3); }
    }
    private static void ValidateRoutes(JsonElement routes)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in routes.EnumerateArray())
        {
            var id = Text(route, "id"); var origin = Text(route, "from"); var destination = Text(route, "to");
            Require(IsRouteId(id) && ids.Add(id) && origin.Length is > 0 and <= 160 && destination.Length is > 0 and <= 160
                && origin.All(c => c >= ' ') && destination.All(c => c >= ' '));
            var points = route.GetProperty("points"); ValidatePolyline(points);
            foreach (var key in new[] { "transport_points", "disconnected_points" })
            {
                if (route.TryGetProperty(key, out var parked) && parked.ValueKind != JsonValueKind.Null) { ValidatePolyline(parked); }
            }
            if (route.TryGetProperty("point_lift_weights", out var weights))
            {
                Require(weights.ValueKind == JsonValueKind.Object);
                foreach (var weight in weights.EnumerateObject())
                {
                    Require(Regex.IsMatch(weight.Name, "^[1-6]$") && weight.Value.ValueKind == JsonValueKind.Array
                        && weight.Value.GetArrayLength() == points.GetArrayLength());
                    Require(weight.Value.EnumerateArray().All(x => x.TryGetDouble(out var number) && double.IsFinite(number) && number is >= 0 and <= 1));
                }
            }
            if (route.TryGetProperty("moving_points", out var moving))
            {
                Require(moving.ValueKind == JsonValueKind.Object);
                foreach (var point in moving.EnumerateObject())
                {
                    Require(Regex.IsMatch(point.Name, "^[0-9]{1,5}$") && int.Parse(point.Name, System.Globalization.CultureInfo.InvariantCulture) < points.GetArrayLength()
                        && point.Value.ValueKind == JsonValueKind.String && point.Value.GetString()!.Length <= 160);
                }
            }
            if (route.TryGetProperty("moving_endpoints", out var endpoints))
            {
                Require(endpoints.ValueKind == JsonValueKind.Array && endpoints.GetArrayLength() <= 32);
                foreach (var endpoint in endpoints.EnumerateArray())
                {
                    var name = Text(endpoint, "endpoint"); Require(name == origin || name == destination);
                }
            }
        }
    }
    private static void ValidateTelemetryMapping(JsonElement mapping, HashSet<string> names)
    {
        Require(mapping.ValueKind == JsonValueKind.Object && IsHash(Text(mapping, "revision"))
            && mapping.GetProperty("schemaVersion").GetInt32() == 1 && Text(mapping, "hardwareProfile") == "B-effects");
        var slots = mapping.GetProperty("slots"); Require(slots.ValueKind == JsonValueKind.Array && slots.GetArrayLength() <= 6);
        var used = new HashSet<int>();
        foreach (var entry in slots.EnumerateArray())
        {
            var slot = entry.GetProperty("slot").GetInt32(); Require(slot is >= 1 and <= 6 && used.Add(slot));
            var components = entry.GetProperty("components"); Require(components.ValueKind == JsonValueKind.Object);
            Require(components.EnumerateObject().All(x => x.Value.ValueKind == JsonValueKind.String && names.Contains(x.Value.GetString()!)));
            foreach (var key in new[] { "liftComponents", "rotationComponents" })
            {
                var parts = entry.GetProperty(key);
                Require(parts.ValueKind == JsonValueKind.Array && parts.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String && names.Contains(x.GetString()!)));
            }
            var channel = entry.GetProperty("channels"); Require(channel.ValueKind == JsonValueKind.Object);
            var servo = channel.GetProperty("servoPca").GetInt32(); var vibration = channel.GetProperty("vibrationPca").GetInt32();
            var start = channel.GetProperty("ledStart").GetInt32(); var count = channel.GetProperty("ledCount").GetInt32();
            Require(servo is >= 0 and <= 15 && vibration is >= 0 and <= 15 && start >= 0 && count >= 1 && (long)start + count <= 36);
        }
        var law = mapping.GetProperty("kinematics"); Require(law.ValueKind == JsonValueKind.Object);
        var liftMax = law.GetProperty("liftMaxMm").GetDouble(); var angleMin = law.GetProperty("angleMinDegrees").GetDouble(); var angleMax = law.GetProperty("angleMaxDegrees").GetDouble();
        Require(double.IsFinite(liftMax) && double.IsFinite(angleMin) && double.IsFinite(angleMax) && liftMax > 0 && angleMax > angleMin);
        var samples = law.GetProperty("samples"); Require(samples.ValueKind == JsonValueKind.Array && samples.GetArrayLength() is >= 2 and <= 2000);
        var lastAngle = double.NegativeInfinity; var lastLift = double.NegativeInfinity;
        foreach (var sample in samples.EnumerateArray())
        {
            var angle = sample.GetProperty("angleDegrees").GetDouble(); var lift = sample.GetProperty("liftMm").GetDouble();
            Require(double.IsFinite(angle) && double.IsFinite(lift) && angle > lastAngle && lift >= lastLift);
            lastAngle = angle; lastLift = lift;
        }
        Require(Math.Abs(samples[0].GetProperty("liftMm").GetDouble()) <= .001 && Math.Abs(lastLift - liftMax) <= .001);
    }
}
