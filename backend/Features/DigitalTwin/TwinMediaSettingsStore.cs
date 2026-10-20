using System.Globalization;
using System.Text.Json;

namespace Kufr.Backend.Features.DigitalTwin;

internal sealed class TwinMediaSettingsStore
{
    private readonly Dictionary<string, TwinMediaSettings> settings = new(StringComparer.Ordinal);
    private readonly IConfiguration configuration;
    private readonly Lock gate = new();
    private readonly string? root;
    public TwinMediaSettingsStore(IConfiguration configuration)
    {
        this.configuration = configuration;
        root = configuration["DigitalTwin:DataRoot"];
        foreach (var entry in configuration.GetSection("DigitalTwin:Instances").GetChildren())
        {
            if (entry["Mode"] != "simulation" || entry["Profile"] != "integration") { continue; }
            var current = new TwinMediaSettings("0", "fake", "cedar", 1, false, "main", Availability());
            var path = PathFor(entry.Key);
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 4096) { throw new InvalidDataException("Twin settings limit."); }
                var persisted = JsonSerializer.Deserialize<TwinMediaSettings>(File.ReadAllBytes(path), TwinSnapshot.JsonOptions);
                if (persisted is null || !Valid(persisted.Revision, persisted.Engine, persisted.Voice, persisted.Speed, persisted.PresentationVariant))
                { throw new InvalidDataException("Invalid persisted Twin settings."); }
                current = persisted with { Availability = Availability() };
            }
            settings.Add(entry.Key, current);
        }
    }
    internal bool IsFake(string id) { lock (gate) { return settings.TryGetValue(id, out var value) && value.Engine == "fake"; } }
    internal TwinMediaSettings? Read(string id) { lock (gate) { return settings.GetValueOrDefault(id); } }
    internal (TwinMediaSettings? Settings, string? Error) Change(string id, TwinMediaSettingsChange change)
    {
        lock (gate)
        {
            if (!settings.TryGetValue(id, out var previous)) { return (null, "settings_unavailable"); }
            if (change.ExpectedRevision != previous.Revision) { return (null, "settings_revision_mismatch"); }
            if (!Valid(change.ExpectedRevision, change.Engine, change.Voice, change.Speed, change.PresentationVariant))
            { return (null, "invalid_settings"); }
            if (change.Engine != "fake" && !previous.Availability.Realtime) { return (null, "provider_not_authorized_or_configured"); }
            var revision = ulong.Parse(previous.Revision, CultureInfo.InvariantCulture);
            if (revision == ulong.MaxValue) { return (null, "settings_revision_limit"); }
            var next = new TwinMediaSettings((revision + 1).ToString(CultureInfo.InvariantCulture), change.Engine,
                change.Voice, change.Speed, change.PhotoConsent, change.PresentationVariant, previous.Availability);
            var path = PathFor(id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".pending";
            try
            {
                using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(file, next, TwinSnapshot.JsonOptions);
                    file.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            catch (IOException) { return (null, "settings_storage_failure"); }
            settings[id] = next;
            return (next, null);
        }
    }
    private string PathFor(string id) => Path.Combine(root ?? throw new InvalidOperationException("Missing Twin data root."),
        "simulation", id, "media-settings.json");
    private TwinMediaAvailability Availability()
    {
        var provider = configuration.GetValue<bool>("DigitalTwin:ExternalServicesAuthorized")
            && !string.IsNullOrWhiteSpace(configuration["OPENAI_API_KEY"]);
        return new(true, provider, provider, true, true, true);
    }
    private static bool Valid(string revision, string engine, string voice, double speed, string variant) =>
        TwinSnapshot.Decimal(revision, out _) && engine is "fake" or "realtime" or "gpt-live"
        && voice is "cedar" or "marin" or "coral" && double.IsFinite(speed) && speed is >= .8 and <= 1.2
        && (engine != "gpt-live" || speed == 1) && variant is "main" or "short";
}
