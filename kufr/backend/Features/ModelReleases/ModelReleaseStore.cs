using System.Text.Json;

namespace Kufr.Backend.Features.ModelReleases;

internal sealed record ModelActiveReference(int SchemaVersion, string ModelId, string BundleHash, string ManifestPath);
internal sealed record ModelCatalog(bool Enabled, ModelActiveReference? Active, IReadOnlyList<ModelRelease> Releases,
    int InvalidReleases, bool Truncated, string Integrity = "checked_on_selection");
internal sealed record SelectModelRelease(string BundleHash, string? ExpectedActiveHash, bool Confirm);

internal sealed class ModelReleaseStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public bool Enabled { get; }
    internal string Root { get; }

    public ModelReleaseStore(IConfiguration configuration)
    {
        Enabled = configuration.GetValue<bool>("ModelReleases:Enabled");
        Root = configuration["ModelReleases:Root"] ?? "";
        if (Enabled && !Path.IsPathFullyQualified(Root))
        {
            throw new InvalidOperationException("ModelReleases requires a dedicated absolute durable model directory.");
        }
        if (Enabled)
        {
            Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root));
            if (Path.GetPathRoot(Root) == Root) { throw new InvalidOperationException("ModelReleases cannot use a filesystem root."); }
        }
    }

    public async Task<ModelCatalog> ListAsync(CancellationToken cancellationToken)
    {
        if (!Enabled) { return new(false, null, [], 0, false); }
        CheckRoot();
        var active = await ReadActiveAsync(cancellationToken);
        var entries = Directory.EnumerateDirectories(Path.Combine(Root, "bundles")).Take(101).ToArray();
        var releases = new List<ModelRelease>(); var invalid = 0;
        foreach (var directory in entries.Take(100).OrderBy(x => x, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = Path.GetFileName(directory);
            if (!ModelBundleValidation.IsHash(hash)) { continue; }
            try { releases.Add(await ReadReleaseAsync(hash, false, cancellationToken)); }
            catch (ModelReleaseException) { invalid++; }
        }
        return new(true, active, releases, invalid, entries.Length > 100);
    }

    public async Task<ModelActiveReference> SelectAsync(SelectModelRelease request, CancellationToken cancellationToken)
    {
        if (!Enabled) { throw new ModelReleaseException("model_releases_disabled"); }
        if (!request.Confirm) { throw new ModelReleaseException("confirmation_required"); }
        if (!ModelBundleValidation.IsHash(request.BundleHash)
            || request.ExpectedActiveHash is not null && !ModelBundleValidation.IsHash(request.ExpectedActiveHash))
        {
            throw new ModelReleaseException("model_release_invalid");
        }
        CheckRoot();
        var release = await ReadReleaseAsync(request.BundleHash, true, cancellationToken);
        if (!release.CanSelect) { throw new ModelReleaseException("verified_model_required"); }
        // This exclusive file is shared with the local publisher. No automatic
        // stale-lock deletion can authorize concurrent writers or lose selection.
        var lockPath = Path.Combine(Root, ".activation-lock");
        FileStream activationLock;
        try { activationLock = new(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        catch (IOException)
        {
            throw new ModelReleaseException(File.Exists(lockPath) ? "model_activation_busy" : "model_store_unavailable");
        }
        try
        {
            var active = await ReadActiveAsync(cancellationToken);
            if (active?.BundleHash != request.ExpectedActiveHash) { throw new ModelReleaseException("active_model_changed"); }
            var next = new ModelActiveReference(1, release.ModelId, release.BundleHash, $"bundles/{release.BundleHash}/model-bundle.json");
            if (active == next) { return next; }
            // Recheck manifest identity immediately before activation. Models
            // must remain immutable and writable only by trusted publishers.
            var rechecked = await ReadReleaseAsync(request.BundleHash, false, cancellationToken);
            ModelBundleValidation.Require(rechecked == release);
            if (active is not null) { await WriteAtomicAsync("previous.json", active, cancellationToken); }
            await WriteAtomicAsync("active.json", next, cancellationToken);
            return next;
        }
        finally { await activationLock.DisposeAsync(); File.Delete(lockPath); }
    }

    private void CheckRoot()
    {
        try
        {
            ModelBundleValidation.Require(Directory.Exists(Root) && (File.GetAttributes(Root) & FileAttributes.ReparsePoint) == 0);
            ModelBundleValidation.CheckRealPath(Root, "bundles", true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ModelReleaseException)
        {
            throw new ModelReleaseException("model_store_unavailable");
        }
    }
    private async Task<ModelRelease> ReadReleaseAsync(string hash, bool verifyContents, CancellationToken cancellationToken)
    {
        try
        {
            ModelBundleValidation.CheckRealPath(Root, "bundles/" + hash, true);
            return await ModelBundleValidation.ReadAsync(Path.Combine(Root, "bundles", hash), hash, verifyContents, cancellationToken);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new ModelReleaseException("model_release_invalid");
        }
    }
    private async Task<ModelActiveReference?> ReadActiveAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(Root, "active.json");
        if (!File.Exists(path)) { return null; }
        try
        {
            ModelBundleValidation.CheckRealPath(Root, "active.json");
            ModelBundleValidation.Require(new FileInfo(path).Length is > 0 and <= 4096);
            var active = JsonSerializer.Deserialize<ModelActiveReference>(await File.ReadAllBytesAsync(path, cancellationToken), Json);
            ModelBundleValidation.Require(active is not null && active.SchemaVersion == 1 && ModelBundleValidation.IsHash(active.BundleHash)
                && active.ManifestPath == $"bundles/{active.BundleHash}/model-bundle.json" && active.ModelId is { Length: > 0 and <= 160 });
            return active;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ModelReleaseException)
        {
            throw new ModelReleaseException("active_model_invalid");
        }
    }
    private async Task WriteAtomicAsync(string name, ModelActiveReference value, CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(Root, ".activation-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, value, Json, cancellationToken);
                await stream.FlushAsync(cancellationToken); stream.Flush(true);
            }
            File.Move(temporary, Path.Combine(Root, name), true);
        }
        finally { File.Delete(temporary); }
    }
}
