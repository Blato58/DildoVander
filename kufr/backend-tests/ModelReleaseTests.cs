using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.ModelReleases;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

internal static class ModelReleaseTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "dv-model-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "bundles"));
        try
        {
            var disabled = new ModelReleaseStore(new ConfigurationBuilder().Build());
            Check(!(await disabled.ListAsync(default)).Enabled, "default disabled");
            var store = Store(root);
            var first = WriteBundle(root, "synthetic-source-a");
            var second = WriteBundle(root, "synthetic-source-b");
            Check(first != second, "manifest identity pins independent model source provenance");
            await FailsAsync(() => store.SelectAsync(new(first, null, false), default), "confirmation_required");
            await FailsAsync(() => store.SelectAsync(new("../escape", null, true), default), "model_release_invalid");
            var active = await store.SelectAsync(new(first, null, true), default);
            await FailsAsync(() => store.SelectAsync(new(second, null, true), default), "active_model_changed");
            Check((await store.ListAsync(default)).Active == active, "stale selection leaves active unchanged");
            await store.SelectAsync(new(second, first, true), default);
            await store.SelectAsync(new(second, second, true), default);
            using (var previous = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "previous.json"))))
            {
                Check(previous.RootElement.GetProperty("bundleHash").GetString() == first, "duplicate selection retains compatible rollback");
            }
            foreach (var routeId in new[] { "P12/+", "<P12+>", "P12+\\escape", "P12+\n" })
            {
                var invalidRoute = WriteBundle(root, "synthetic-invalid-route", routeId: routeId);
                await FailsAsync(() => store.SelectAsync(new(invalidRoute, second, true), default), "model_release_invalid");
            }
            foreach (var sourcePath in new[] { "../MONT.md", "/MONT.md", "a//b.md", "a\\b.md", "MONT.md\n" })
            {
                var invalidEvidence = WriteBundle(root, "synthetic-invalid-evidence", sourceEvidencePath: sourcePath);
                await FailsAsync(() => store.SelectAsync(new(invalidEvidence, second, true), default), "model_release_invalid");
            }
            var unverified = WriteBundle(root, "synthetic-unverified", false);
            await FailsAsync(() => store.SelectAsync(new(unverified, second, true), default), "verified_model_required");
            var corrupt = WriteBundle(root, "synthetic-corrupt");
            var data = Path.Combine(root, "bundles", corrupt, "model.json");
            var bytes = await File.ReadAllBytesAsync(data); bytes[^1] = (byte)' '; await File.WriteAllBytesAsync(data, bytes);
            await FailsAsync(() => store.SelectAsync(new(corrupt, second, true), default), "model_release_invalid");
            var extra = WriteBundle(root, "synthetic-extra");
            await File.WriteAllTextAsync(Path.Combine(root, "bundles", extra, "undeclared.txt"), "not part of model");
            await FailsAsync(() => store.SelectAsync(new(extra, second, true), default), "model_release_invalid");
            await File.WriteAllTextAsync(Path.Combine(root, ".activation-lock"), "test external publisher lock");
            await FailsAsync(() => store.SelectAsync(new(first, second, true), default), "model_activation_busy");
            File.Delete(Path.Combine(root, ".activation-lock"));
            Check((await store.ListAsync(default)).Active?.BundleHash == second, "all failed mutations preserve active model");
            var fixture = FindFixture();
            using var fixtureManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(fixture, "model-bundle.json")));
            var fixtureHash = fixtureManifest.RootElement.GetProperty("bundleHash").GetString()!;
            var fixtureRelease = await ModelBundleValidation.ReadAsync(fixture, fixtureHash, true, default);
            Check(!fixtureRelease.CanSelect, "actual app fixture and canonical hash validate across runtimes but cannot activate");
            var corpus = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixture, "../../../../tests/fixtures/model-bundle-rejections.json")))!.AsArray();
            foreach (var item in corpus)
            {
                var candidate = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixture, "model.json")))!;
                foreach (var operation in item!["operations"]!.AsArray())
                {
                    var path = operation!["path"]!.AsArray(); JsonNode parent = candidate;
                    foreach (var part in path.Take(path.Count - 1))
                    {
                        parent = part!.GetValueKind() == JsonValueKind.Number ? parent[part.GetValue<int>()]! : parent[part.GetValue<string>()]!;
                    }
                    var last = path[^1]!;
                    if (last.GetValueKind() == JsonValueKind.Number) { parent[last.GetValue<int>()] = operation["value"]?.DeepClone(); }
                    else { parent[last.GetValue<string>()] = operation["value"]?.DeepClone(); }
                }
                var rejected = WriteBundleBytes(root, JsonSerializer.SerializeToUtf8Bytes(candidate), "shared-rejection-" + item["name"]!.GetValue<string>(), true);
                await FailsAsync(() => store.SelectAsync(new(rejected, second, true), default), "model_release_invalid");
                Check((await store.ListAsync(default)).Active?.BundleHash == second, "shared invalid corpus never changes activation");
            }
            Console.WriteLine("Model releases: disabled mode, canonical schema/hash, verified-only selection, stale/duplicate actions, atomic pointer, lock and failure preservation passed (isolated synthetic stores)");
        }
        finally { Directory.Delete(root, true); }
    }

    // Called from the real Identity passkey fixture after verified owner
    // enrollment. These are real cookies, origin and antiforgery middleware.
    public static async Task RunAuthorizedAsync(WebApplication app, HttpClient owner, HttpClient anonymous)
    {
        var store = app.Services.GetRequiredService<ModelReleaseStore>();
        Directory.CreateDirectory(Path.Combine(store.Root, "bundles"));
        var first = WriteBundle(store.Root, "synthetic-http-source");
        Check((await anonymous.GetAsync("/app/v1/admin/model-releases")).StatusCode == HttpStatusCode.Unauthorized, "anonymous catalog denied");
        Check((await owner.GetAsync("/app/v1/admin/model-releases")).IsSuccessStatusCode, "verified owner catalog available");
        var noConfirmation = await owner.PostAsJsonAsync("/app/v1/admin/model-releases/active", new SelectModelRelease(first, null, false));
        Check(noConfirmation.StatusCode == HttpStatusCode.BadRequest, "owner must explicitly confirm selection");
        var csrf = owner.DefaultRequestHeaders.GetValues("X-DV-CSRF").Single();
        owner.DefaultRequestHeaders.Remove("X-DV-CSRF");
        Check((await owner.PostAsJsonAsync("/app/v1/admin/model-releases/active", new SelectModelRelease(first, null, true))).StatusCode == HttpStatusCode.BadRequest, "CSRF required for selection");
        owner.DefaultRequestHeaders.Add("X-DV-CSRF", csrf);
        owner.DefaultRequestHeaders.Remove("Origin"); owner.DefaultRequestHeaders.Add("Origin", "https://wrong-origin.invalid");
        Check((await owner.PostAsJsonAsync("/app/v1/admin/model-releases/active", new SelectModelRelease(first, null, true))).StatusCode == HttpStatusCode.Forbidden, "cross-origin selection denied");
        owner.DefaultRequestHeaders.Remove("Origin"); owner.DefaultRequestHeaders.Add("Origin", "https://localhost");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            await db.Sessions.ExecuteUpdateAsync(update => update.SetProperty(x => x.AuthenticatedAt, AccountEndpoints.Now() - 601));
        }
        Check((await owner.PostAsJsonAsync("/app/v1/admin/model-releases/active", new SelectModelRelease(first, null, true))).StatusCode == HttpStatusCode.Forbidden, "recent passkey confirmation required");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            await db.Sessions.ExecuteUpdateAsync(update => update.SetProperty(x => x.AuthenticatedAt, AccountEndpoints.Now()));
        }
        Check((await owner.PostAsJsonAsync("/app/v1/admin/model-releases/active", new SelectModelRelease(first, null, true))).IsSuccessStatusCode, "recent verified owner can activate validated model");
        Check((await owner.PostAsJsonAsync("/app/v1/admin/model-releases/active", new SelectModelRelease(first, null, true))).StatusCode == HttpStatusCode.Conflict, "stale HTTP confirmation cannot overwrite current");
    }

    public static async Task RunDeniedAsync(HttpClient client)
    {
        Check((await client.GetAsync("/app/v1/admin/model-releases")).StatusCode == HttpStatusCode.Forbidden, "non-admin catalog denied");
        Check((await client.PostAsJsonAsync("/app/v1/admin/model-releases/active", new SelectModelRelease(new string('a', 64), null, true))).StatusCode == HttpStatusCode.Forbidden, "non-admin selection denied");
    }

    internal static string WriteBundle(string root, string source, bool verified = true, string routeId = "P12+", string sourceEvidencePath = "MONT\u00c1\u017d.md")
    {
        // Deliberate synthetic verified-format metadata for isolated tests;
        // this never represents actual CAD or physical acceptance evidence.
        var model = new
        {
            modelId = "synthetic-model-tests", revision = "test-r1",
            meshes = new[] { new { id = "part-a", name = "part-a", group = "synthetic", color = new[] { 0.5, 0.5, 0.5 }, bounds = new[] { 0, 0, 0, 1, 1, 0 },
                transform = new[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 }, parentId = (string?)null,
                vertices = new[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, triangles = new[] { 0, 1, 2 } } },
            hinge = new[] { 0, 0, 0 }, mechanism = new { origin_y = 0, origin_z = 0, cam_y = 0, cam_z = 0, dwell_degrees = 0, travel_degrees = 180, lift = 1, count = 1 },
            assemblyGuide = new { steps = new[] { new { id = "step-a", title = "Synthetic assembly test", parts = new[] { "part-a" } } } },
            routes = new[] { new { id = routeId, from = "power:out", to = "board:in", transport_points = (object?)null, disconnected_points = (object?)null, points = new[] { new[] { 0, 0, 0 }, new[] { 1, 1, 1 } } } }, sources = Array.Empty<object>(), connections = Array.Empty<object>(), bomReferences = Array.Empty<object>(),
            presentation = new { target = new[] { 0, 0, 0 } }, twinManifest = (object?)null
        };
        return WriteBundleBytes(root, JsonSerializer.SerializeToUtf8Bytes(model), source, verified, sourceEvidencePath);
    }
    private static string WriteBundleBytes(string root, byte[] bytes, string source, bool verified, string sourceEvidencePath = "MONT\u00c1\u017d.md")
    {
        using var modelDocument = JsonDocument.Parse(bytes);
        var modelId = modelDocument.RootElement.GetProperty("modelId").GetString();
        var revision = modelDocument.RootElement.GetProperty("revision").GetString();
        var modelHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var assets = new List<object> { new { path = "model.json", kind = "model-json", sha256 = modelHash, bytes = bytes.Length } };
        byte[]? verificationBytes = null;
        if (verified)
        {
            var sourceHashes = new Dictionary<string, string> { [sourceEvidencePath] = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source))) };
            verificationBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                result = "PASS_CAD_NOT_PHYSICAL_ACCEPTANCE", physical_test_performed = false, sources = sourceHashes,
                modelEvidence = new { schemaVersion = 1, modelId, modelAssetSha256 = modelHash, sourceRevision = source, sourceHashes }
            });
            assets.Add(new { path = "verification.json", kind = "verification-json", sha256 = Convert.ToHexStringLower(SHA256.HashData(verificationBytes)), bytes = verificationBytes.Length });
        }
        var manifest = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1, ["modelId"] = modelId, ["revision"] = revision,
            ["units"] = "mm", ["coordinates"] = "right-handed-z-up", ["compatibility"] = new { viewerSchema = 1 },
            ["provenance"] = new { kind = "cad-export", sourceRevision = source, cadVerification = verified ? "verified" : "not-verified" },
            ["assets"] = assets
        };
        using var identity = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(manifest));
        var hash = Convert.ToHexStringLower(SHA256.HashData(ModelBundleValidation.CanonicalManifest(identity.RootElement)));
        manifest["bundleHash"] = hash;
        var directory = Path.Combine(root, "bundles", hash); Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "model.json"), bytes);
        if (verificationBytes is not null) { File.WriteAllBytes(Path.Combine(directory, "verification.json"), verificationBytes); }
        File.WriteAllBytes(Path.Combine(directory, "model-bundle.json"), JsonSerializer.SerializeToUtf8Bytes(manifest));
        return hash;
    }
    private static ModelReleaseStore Store(string root) => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ModelReleases:Enabled"] = "true", ["ModelReleases:Root"] = root }).Build());
    private static string FindFixture()
    {
        for (var path = new DirectoryInfo(Environment.CurrentDirectory); path is not null; path = path.Parent)
        {
            var fixture = Path.Combine(path.FullName, "kufr/mobilni-aplikace/src/features/workshop/fixture");
            if (Directory.Exists(fixture)) { return fixture; }
        }
        throw new InvalidOperationException("Run model release tests from the repository checkout");
    }
    private static async Task FailsAsync(Func<Task> action, string code)
    {
        try { await action(); } catch (ModelReleaseException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected model rejection: " + code);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException("Model release: " + message); }
    }
}
