namespace Kufr.Backend.Features.DigitalTwin;

// Reuses the existing deterministic dialogue service only for explicitly
// provisioned simulated identities in a separate backend database.
internal sealed class TwinFakeInputPolicy
{
    private readonly HashSet<string> devices = new(StringComparer.Ordinal);
    private readonly TwinMediaSettingsStore? settings;
    public TwinFakeInputPolicy(IConfiguration configuration, TwinMediaSettingsStore? settings = null)
    {
        this.settings = settings;
        var entries = configuration.GetSection("DigitalTwin:Instances").GetChildren()
            .Where(entry => entry["Mode"] == "simulation" && entry["Profile"] == "integration").ToArray();
        if (entries.Length == 0) { return; }
        var root = configuration["DigitalTwin:DataRoot"];
        var database = configuration["Backend:DatabasePath"];
        if (root is null || database is null || !Path.IsPathFullyQualified(root)
            || !Path.GetFullPath(database).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || configuration.GetValue<bool>("Backend:Memory:Enabled")
            || configuration["Backend:Dialog:Mode"] is not (null or "fake")
                && !(configuration["Backend:Dialog:Mode"] == "responses"
                    && configuration.GetValue<bool>("DigitalTwin:ExternalServicesAuthorized")))
        {
            throw new InvalidOperationException("Synthetic integration dialogue requires an isolated fake backend database.");
        }
        foreach (var entry in entries)
        {
            if (!entry.Key.StartsWith("sim-", StringComparison.Ordinal) || entry["DeviceId"] != entry.Key)
            {
                throw new InvalidOperationException("Synthetic integration identity must be separate from live devices.");
            }
            if (configuration["Backend:Dialog:Mode"] is null or "fake" || settings is not null) { devices.Add(entry.Key); }
        }
    }
    public bool Allows(string deviceId) => devices.Contains(deviceId) && (settings is null || settings.IsFake(deviceId));
}
