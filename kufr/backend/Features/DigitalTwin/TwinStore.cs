using System.Text.Json;
using System.Threading.Channels;

namespace Kufr.Backend.Features.DigitalTwin;

internal sealed record TwinEnvelope(TwinSnapshot Snapshot, DateTimeOffset ReceivedAtUtc);
internal sealed record TwinArchive(TwinEnvelope Latest, string[] RetiredBoots);

internal sealed class TwinStore : BackgroundService
{
    private sealed class Instance
    {
        public TwinEnvelope? Latest;
        public readonly HashSet<string> Retired = new(StringComparer.Ordinal);
        public readonly Queue<TwinEnvelope> History = new();
    }
    private readonly Dictionary<string, Instance> instances = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private readonly TimeProvider clock;
    private readonly string? root;
    private readonly Channel<(string Id, TwinArchive Archive)> writes = Channel.CreateBounded<(string, TwinArchive)>(
        new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private long dropped;
    private long storageErrors;

    public TwinStore(IConfiguration configuration, TimeProvider clock)
    {
        this.clock = clock;
        root = configuration["DigitalTwin:DataRoot"];
        var configured = configuration.GetSection("DigitalTwin:Instances").GetChildren().ToArray();
        if (configured.Length > 16 || configured.Any(entry => !TwinSnapshot.Id(entry.Key)))
        {
            throw new InvalidOperationException("Invalid Digital Twin instance allowlist.");
        }
        if (configured.Length > 0 && (root is null || !Path.IsPathFullyQualified(root)))
        {
            throw new InvalidOperationException("Digital Twin DataRoot must be an absolute isolated path.");
        }
        foreach (var entry in configured)
        {
            var instance = new Instance();
            instances.Add(entry.Key, instance);
            var path = Path.Combine(root!, entry.Key + ".latest.json");
            if (!File.Exists(path)) { continue; }
            if (new FileInfo(path).Length > 65536) { throw new InvalidDataException("Oversized Digital Twin archive."); }
            var archive = JsonSerializer.Deserialize<TwinArchive>(File.ReadAllText(path), TwinSnapshot.JsonOptions);
            if (archive is null || !archive.Latest.Snapshot.Valid() || archive.Latest.Snapshot.Source.InstanceId != entry.Key
                || archive.RetiredBoots.Length > 128 || archive.RetiredBoots.Any(boot => !TwinSnapshot.Id(boot)))
            {
                throw new InvalidDataException("Invalid Digital Twin archive; explicit service inspection required.");
            }
            instance.Latest = archive.Latest;
            instance.Retired.UnionWith(archive.RetiredBoots);
        }
    }

    public string? Accept(TwinSnapshot snapshot)
    {
        if (!snapshot.Valid()) { return "invalid_snapshot"; }
        lock (gate)
        {
            if (!instances.TryGetValue(snapshot.Source.InstanceId, out var instance)) { return "unknown_instance"; }
            var previous = instance.Latest?.Snapshot;
            if (previous is not null)
            {
                if (previous.Source.Mode != snapshot.Source.Mode || previous.Source.DeviceId != snapshot.Source.DeviceId)
                {
                    return "identity_conflict";
                }
                if (previous.Source.BootId != snapshot.Source.BootId)
                {
                    if (instance.Retired.Contains(snapshot.Source.BootId)) { return "retired_boot"; }
                    if (instance.Retired.Count >= 128) { return "boot_history_full"; }
                    instance.Retired.Add(previous.Source.BootId);
                }
                else
                {
                    if (previous.Source != snapshot.Source with
                        { CalibrationRevision = previous.Source.CalibrationRevision, CadManifestRevision = previous.Source.CadManifestRevision })
                    {
                        return "boot_provenance_conflict";
                    }
                    TwinSnapshot.Decimal(previous.Sequence, out var previousSequence);
                    TwinSnapshot.Decimal(snapshot.Sequence, out var sequence);
                    TwinSnapshot.Decimal(previous.TimeMs, out var previousTime);
                    TwinSnapshot.Decimal(snapshot.TimeMs, out var time);
                    if (sequence <= previousSequence)
                    {
                        return sequence == previousSequence && JsonSerializer.Serialize(previous, TwinSnapshot.JsonOptions)
                            == JsonSerializer.Serialize(snapshot, TwinSnapshot.JsonOptions) ? null : "sequence_conflict";
                    }
                    if (time < previousTime) { return "clock_regression"; }
                }
            }
            var envelope = new TwinEnvelope(snapshot, clock.GetUtcNow());
            instance.Latest = envelope;
            instance.History.Enqueue(envelope);
            if (instance.History.Count > 128) { instance.History.Dequeue(); }
            if (!writes.Writer.TryWrite((snapshot.Source.InstanceId, new TwinArchive(envelope, instance.Retired.ToArray()))))
            {
                Interlocked.Increment(ref dropped);
            }
            return null;
        }
    }
    public string[] Ids() { lock (gate) { return instances.Keys.ToArray(); } }
    public TwinEnvelope? Read(string id) { lock (gate) { return instances.GetValueOrDefault(id)?.Latest; } }
    public TwinEnvelope[] Recording(string id)
    {
        lock (gate) { return instances.GetValueOrDefault(id)?.History.ToArray() ?? []; }
    }
    public object View(TwinEnvelope envelope)
    {
        var age = Math.Max(0, (clock.GetUtcNow() - envelope.ReceivedAtUtc).TotalMilliseconds);
        return new { envelope.Snapshot, envelope.ReceivedAtUtc, ageMs = age,
            status = Status(envelope),
            serverDroppedSnapshots = Interlocked.Read(ref dropped).ToString(),
            storageErrors = Interlocked.Read(ref storageErrors).ToString() };
    }
    public string Status(TwinEnvelope? envelope)
    {
        if (envelope is null) { return "connecting"; }
        var age = Math.Max(0, (clock.GetUtcNow() - envelope.ReceivedAtUtc).TotalMilliseconds);
        return age > 10000 ? "offline" : age > 2000 ? "stale" : "online";
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (root is null) { return; }
        Directory.CreateDirectory(root);
        var recordedState = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            await foreach (var entry in writes.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    var json = JsonSerializer.Serialize(entry.Archive, TwinSnapshot.JsonOptions);
                    var latest = Path.Combine(root, entry.Id + ".latest.json");
                    await File.WriteAllTextAsync(latest + ".new", json, stoppingToken);
                    File.Move(latest + ".new", latest, true);
                    // Record state changes, not endless identical idle frames.
                    // Full latest snapshots still update independently of recording.
                    var snapshot = entry.Archive.Latest.Snapshot;
                    var state = JsonSerializer.Serialize(new { snapshot.Source, snapshot.Revision,
                        snapshot.Reported, snapshot.Model, snapshot.Trace, snapshot.Transport }, TwinSnapshot.JsonOptions);
                    if (recordedState.GetValueOrDefault(entry.Id) != state)
                    {
                        // Explicit manual deletion policy: records are never aged out.
                        await File.AppendAllTextAsync(Path.Combine(root, entry.Id + ".jsonl"),
                            JsonSerializer.Serialize(entry.Archive.Latest, TwinSnapshot.JsonOptions) + "\n", stoppingToken);
                        recordedState[entry.Id] = state;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Interlocked.Increment(ref storageErrors);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
