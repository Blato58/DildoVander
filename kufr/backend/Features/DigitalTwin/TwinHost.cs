using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using Kufr.Backend.Features.Access;

namespace Kufr.Backend.Features.DigitalTwin;

internal sealed record TwinCommand(string Action, int? Slot = null, string? Variant = null,
    string? OperationId = null, string? Text = null, bool? Paused = null, double? Speed = null,
    int? StepMs = null, string? Fault = null);
internal sealed record TwinCommandResult(bool Accepted, string Code, TwinSnapshot? Snapshot = null, JsonElement? ExternalRequest = null);

// This adapter schedules injected time and forwards inputs; all device decisions
// and output timing remain in the production C++ owners in the child process.
internal sealed class TwinHost : BackgroundService
{
    private sealed record Pending(string Line, TaskCompletionSource<TwinCommandResult> Result, TwinCommand? Clock = null);
    private sealed class Host(string id, string executable, string journal, string build, string profile, string? cad)
    {
        public readonly string Id = id, Executable = executable, Journal = journal, Build = build;
        public readonly string Profile = profile;
        public readonly string? Cad = cad;
        public bool IntegrationBusy;
        public string? ConversationId, ConversationBoot, ConversationGeneration;
        public readonly Channel<Pending> Commands = Channel.CreateBounded<Pending>(16);
        public string? Owner;
        public DateTimeOffset OwnerExpires;
        public bool Paused;
        public double Speed = 1;
        public ulong TimeMs;
        public bool Ready;
        public string Error = "starting";
        public Pending? UrgentStop;
        public readonly Lock Gate = new();
    }
    private readonly Dictionary<string, Host> hosts = new(StringComparer.Ordinal);
    private readonly TwinStore store;
    private readonly TimeProvider clock;
    private readonly DeviceAccessStore devices;
    private readonly Uri? integrationUrl;
    public TwinHost(IConfiguration configuration, TwinStore store, TimeProvider clock, DeviceAccessStore devices)
    {
        this.store = store;
        this.clock = clock;
        this.devices = devices;
        var root = configuration["DigitalTwin:DataRoot"];
        foreach (var entry in configuration.GetSection("DigitalTwin:Instances").GetChildren())
        {
            if (entry["Mode"] != "simulation") { continue; }
            var executable = entry["Executable"];
            if (executable is null) { continue; }
            var build = entry["BuildId"];
            if (root is null || executable is null || !Path.IsPathFullyQualified(executable)
                || !File.Exists(executable) || !TwinSnapshot.Id(build) || hosts.Count >= 2)
            {
                throw new InvalidOperationException("Invalid native Digital Twin host configuration.");
            }
            var directory = Path.Combine(root, "simulation", entry.Key);
            Directory.CreateDirectory(directory);
            var profile = entry["Profile"] ?? "deterministic";
            if (profile is not ("deterministic" or "integration")) { throw new InvalidOperationException("Invalid Twin input profile."); }
            if (profile == "integration")
            {
                var database = configuration["Backend:DatabasePath"];
                var dataPrefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!entry.Key.StartsWith("sim-", StringComparison.Ordinal)
                    || database is null || !Path.GetFullPath(database).StartsWith(dataPrefix, StringComparison.Ordinal)
                    || configuration.GetValue<bool>("Backend:Memory:Enabled")
                    || configuration["Backend:Dialog:Mode"] is not (null or "fake")
                        && !(configuration["Backend:Dialog:Mode"] == "responses"
                            && configuration.GetValue<bool>("DigitalTwin:ExternalServicesAuthorized"))
                    || !configuration.GetSection("Devices").GetChildren().Any(device => device["DeviceId"] == entry.Key)
                    || !Uri.TryCreate(configuration["DigitalTwin:IntegrationBackendUrl"], UriKind.Absolute, out integrationUrl)
                    || !integrationUrl.IsLoopback || integrationUrl.Scheme != "http" || integrationUrl.AbsolutePath != "/")
                {
                    throw new InvalidOperationException("Integration Twin requires an isolated local backend/database/device identity and explicit authorization for external services.");
                }
            }
            var cad = entry["CadManifestRevision"];
            if (cad is not null && (cad.Length != 64 || cad.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))))
            {
                throw new InvalidOperationException("Invalid configured CAD identity.");
            }
            hosts.Add(entry.Key, new Host(entry.Key, executable, Path.Combine(directory, "journal.bin"), build!, profile, cad));
        }
    }
    public bool IsSimulation(string id) => hosts.ContainsKey(id);
    public bool IsReady() => hosts.Values.All(host => host.Ready);
    public object Health() => hosts.Values.Select(host => new { instanceId = host.Id, ready = host.Ready, error = host.Error }).ToArray();
    public async Task<TwinCommandResult> CommandAsync(string id, TwinSession session, TwinCommand command, CancellationToken cancellation)
    {
        if (session.Principal.Role != "control" || session.Principal.Mode != "simulation"
            || session.Principal.InstanceId != id || !hosts.TryGetValue(id, out var host))
        {
            return new(false, "control_forbidden");
        }
        string? line = null;
        var completion = new TaskCompletionSource<TwinCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (host.Gate)
        {
            if (command.Action != "stop" && host.Owner is not null && host.Owner != session.Id && host.OwnerExpires > clock.GetUtcNow())
            {
                return new(false, "control_owned");
            }
            if (!host.Ready) { return new(false, "host_unavailable"); }
            if (command.Action == "release")
            {
                host.Owner = null;
                return new(true, "control-released");
            }
            var previousTime = host.TimeMs;
            var previousPaused = host.Paused;
            var previousSpeed = host.Speed;
            if (command.Action == "clock")
            {
                if (host.Profile != "deterministic") { return new(false, "integration-clock-is-wall-time"); }
                if (command.Speed is < 0.1 or > 8 || command.Speed.HasValue && !double.IsFinite(command.Speed.Value)
                    || command.StepMs is < 0 or > 60000 || command.StepMs > 0 && !(command.Paused ?? host.Paused))
                {
                    return new(false, "invalid_clock");
                }
                // The owner applies this typed relative clock input only after
                // every earlier admitted command. HTTP never mutates its time.
                line = "CLOCK";
            }
            else if (command.Action == "text" && host.Profile == "deterministic" && command.Slot is >= 1 and <= 6
                && command.Variant is "main" or "short" && Guid.TryParseExact(command.OperationId, "D", out _)
                && command.Text is { Length: > 0 and <= 1024 })
            {
                line = $"TEXT {command.OperationId} {command.Slot} {command.Variant}";
            }
            else if (command.Action == "integrationText" && host.Profile == "integration"
                && command.Text is { Length: > 0 and <= 1024 } && Guid.TryParseExact(command.OperationId, "D", out _))
            {
                if (host.IntegrationBusy) { return new(false, "integration-busy"); }
                host.IntegrationBusy = true;
                line = "SNAPSHOT";
            }
            else if (command.Action == "stop") { line = "STOP"; }
            else if (command.Action == "inspect") { line = "INSPECT"; }
            else if (command.Action == "dance" && Guid.TryParseExact(command.OperationId, "D", out _))
            {
                line = "DANCE " + command.OperationId;
            }
            else if (command.Action == "danceStop") { line = "DANCE_STOP"; }
            else if (command.Action == "fault" && command.Fault is "none" or "servo" or "led" or "journal" or "jam"
                && command.Slot is >= 1 and <= 6)
            {
                line = $"FAULT {command.Fault} {command.Slot}";
            }
            if (line is null) { return new(false, "unsupported_or_invalid_input"); }
            if (line == "STOP")
            {
                if (host.UrgentStop is not null) { completion = host.UrgentStop.Result; }
                else { host.UrgentStop = new Pending(line, completion); }
            }
            else if (!host.Commands.Writer.TryWrite(new Pending(line, completion, command.Action == "clock" ? command : null)))
            {
                host.TimeMs = previousTime;
                host.Paused = previousPaused;
                host.Speed = previousSpeed;
                if (command.Action == "integrationText") { host.IntegrationBusy = false; }
                return new(false, "command_queue_full");
            }
            // Any scoped controller may stop this virtual device. Stop does not
            // transfer ordinary command ownership to a newly signed-in session.
            if (command.Action != "stop")
            {
                host.Owner = session.Id;
                host.OwnerExpires = session.Expires;
            }
        }
        // A disconnected HTTP observer cannot cancel an already admitted input.
        if (command.Action != "integrationText")
        {
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation);
            return result with { ExternalRequest = null };
        }
        // Once admitted, a closing tab cannot abandon the pending device input.
        // Network work still has its own bounded wall-clock deadline.
        try
        {
            var accepted = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!accepted.Accepted || accepted.Snapshot is null) { return accepted with { ExternalRequest = null }; }
            return await IntegrationAsync(host, command, accepted.Snapshot);
        }
        finally
        {
            try { await InternalAsync(host, "INTEGRATION_CANCEL"); }
            catch (TimeoutException) { /* The host remains unavailable, not reset. */ }
            lock (host.Gate) { host.IntegrationBusy = false; }
        }
    }
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(hosts.Values.Select(host => RunAsync(host, stoppingToken)));
    private async Task RunAsync(Host host, CancellationToken cancellation)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(host.Executable)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        } };
        foreach (var argument in new[] { "--instance", host.Id, "--boot", Guid.NewGuid().ToString("D"),
            "--journal", host.Journal, "--build", host.Build }) { process.StartInfo.ArgumentList.Add(argument); }
        if (host.Cad is not null) { process.StartInfo.ArgumentList.Add("--cad"); process.StartInfo.ArgumentList.Add(host.Cad); }
        var started = false;
        try
        {
            process.Start();
            started = true;
            // Drain errors without recording arbitrary native output/private data.
            var drain = DrainAsync(process.StandardError, cancellation);
            await ExchangeAsync(host, process, "SNAPSHOT", cancellation);
            host.Ready = true;
            host.Error = "none";
            var previous = Stopwatch.GetTimestamp();
            var lastPublish = previous;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            while (await timer.WaitForNextTickAsync(cancellation))
            {
                var now = Stopwatch.GetTimestamp();
                var elapsed = Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds;
                previous = now;
                Pending? urgent;
                lock (host.Gate) { urgent = host.UrgentStop; host.UrgentStop = null; }
                if (urgent is not null)
                {
                    urgent.Result.TrySetResult(await HandlePendingAsync(host, process, urgent, cancellation));
                    while (host.Commands.Reader.TryRead(out var obsolete)) { obsolete.Result.TrySetResult(new(false, "cancelled-by-stop")); }
                }
                for (var handled = 0; handled < 4 && host.Commands.Reader.TryRead(out var pending); handled++)
                {
                    pending.Result.TrySetResult(await HandlePendingAsync(host, process, pending, cancellation));
                }
                string line;
                lock (host.Gate)
                {
                    if (host.Paused)
                    {
                        if (Stopwatch.GetElapsedTime(lastPublish, now).TotalMilliseconds < 250) { continue; }
                        line = "SNAPSHOT";
                    }
                    else
                    {
                        // Deterministic time pauses during host scheduling gaps.
                        // Wall-clock network timeouts never use this clock.
                        var advance = (ulong)Math.Max(1, Math.Round((host.Profile == "integration" ? elapsed : Math.Min(elapsed, 1000)) * host.Speed));
                        if (advance > 60000) { throw new InvalidDataException("Integration owner wall-time gap exceeded native clock limit."); }
                        if (host.TimeMs > 4102444800000UL - advance) { host.Paused = true; continue; }
                        host.TimeMs += advance;
                        line = "TICK " + host.TimeMs.ToString(CultureInfo.InvariantCulture);
                    }
                }
                var result = await ExchangeAsync(host, process, line, cancellation,
                    Stopwatch.GetElapsedTime(lastPublish, now).TotalMilliseconds >= 200);
                if (!result.Accepted) { throw new InvalidDataException("Automatic host tick rejected."); }
                if (Stopwatch.GetElapsedTime(lastPublish, now).TotalMilliseconds >= 200) { lastPublish = now; }
            }
            await drain;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or JsonException or TimeoutException)
        {
            host.Error = "host_protocol_failure";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            host.Ready = false;
            lock (host.Gate) { host.UrgentStop?.Result.TrySetResult(new(false, "host_unavailable")); host.UrgentStop = null; }
            while (host.Commands.Reader.TryRead(out var pending)) { pending.Result.TrySetResult(new(false, "host_unavailable")); }
            if (started && !process.HasExited) { process.Kill(true); }
            // Restart is explicit service restart and retains the durable journal.
        }
    }
    private async Task<TwinCommandResult> ExchangeAsync(Host host, Process process, string line, CancellationToken cancellation, bool publish = true)
    {
        await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellation);
        await process.StandardInput.FlushAsync(cancellation);
        var output = await process.StandardOutput.ReadLineAsync(cancellation).AsTask().WaitAsync(TimeSpan.FromSeconds(3), cancellation);
        if (output is null || output.Length > 32768) { throw new InvalidDataException("Invalid host response."); }
        // Native commandResult is a transport acknowledgement, not a measured result.
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        var snapshotJson = root.TryGetProperty("snapshot", out var nested) ? nested.GetRawText() : output;
        if (!root.TryGetProperty("snapshot", out _) && (root.TryGetProperty("commandResult", out _) || root.TryGetProperty("externalRequest", out _)))
        {
            var fields = root.EnumerateObject().Where(field => field.Name is not ("commandResult" or "externalRequest"))
                .ToDictionary(field => field.Name, field => field.Value.Clone());
            snapshotJson = JsonSerializer.Serialize(fields);
        }
        var snapshot = JsonSerializer.Deserialize<TwinSnapshot>(snapshotJson, TwinSnapshot.JsonOptions)
            ?? throw new InvalidDataException("Missing host snapshot.");
        if (!snapshot.Valid() || snapshot.Source.InstanceId != host.Id || snapshot.Source.Mode != "simulation")
        {
            throw new InvalidDataException("Invalid host identity.");
        }
        TwinSnapshot.Decimal(snapshot.TimeMs, out var observedTime);
        lock (host.Gate) { host.TimeMs = observedTime; }
        if (publish && store.Accept(snapshot) is not null) { throw new InvalidDataException("Host sequence rejected."); }
        bool accepted = true;
        var code = "accepted";
        if (root.TryGetProperty("commandResult", out var result))
        {
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("accepted", out var value))
            {
                accepted = value.GetBoolean();
                code = result.TryGetProperty("code", out var codeValue) ? codeValue.GetString() ?? "unknown" : "unknown";
            }
            else if (result.ValueKind is JsonValueKind.True or JsonValueKind.False) { accepted = result.GetBoolean(); }
        }
        return new(accepted, code, snapshot, root.TryGetProperty("externalRequest", out var external) ? external.Clone() : null);
    }
    private async Task<TwinCommandResult> InternalAsync(Host host, string line)
    {
        if (!host.Ready) { return new(false, "host_unavailable"); }
        var completion = new TaskCompletionSource<TwinCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!host.Commands.Writer.TryWrite(new Pending(line, completion))) { return new(false, "command_queue_full"); }
        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
    private Task<TwinCommandResult> HandlePendingAsync(Host host, Process process, Pending pending, CancellationToken cancellation)
    {
        var line = pending.Line;
        if (pending.Clock is { } command)
        {
            lock (host.Gate)
            {
                var paused = command.Paused ?? host.Paused;
                if (command.StepMs > 0 && !paused) { return Task.FromResult(new TwinCommandResult(false, "step-requires-pause")); }
                var step = (ulong)(command.StepMs ?? 0);
                if (host.TimeMs > 4102444800000UL - step) { return Task.FromResult(new TwinCommandResult(false, "clock_limit")); }
                host.Paused = paused;
                host.Speed = command.Speed ?? host.Speed;
                line = step > 0 ? "TICK " + (host.TimeMs + step).ToString(CultureInfo.InvariantCulture) : "SNAPSHOT";
            }
        }
        return ExchangeAsync(host, process, line, cancellation);
    }
    private async Task<TwinCommandResult> IntegrationAsync(Host host, TwinCommand command, TwinSnapshot initial)
    {
        using var http = new HttpClient { BaseAddress = integrationUrl, Timeout = TimeSpan.FromSeconds(5) };
        using var deadlineCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var cancellation = deadlineCancellation.Token;
        var token = devices.IssueToken(host.Id);
        if (token is null) { return new(false, "isolated-token-unavailable"); }
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        try
        {
            if (host.ConversationId is null || host.ConversationBoot != initial.Source.BootId
                || host.ConversationGeneration != initial.Reported.Generation)
            {
                if (host.ConversationId is not null)
                {
                    using var closed = await http.DeleteAsync("v1/conversations/" + host.ConversationId, cancellation);
                }
                using var create = new HttpRequestMessage(HttpMethod.Post, "v1/conversations")
                {
                    Content = JsonContent.Create(new { deviceId = host.Id, bootId = initial.Source.BootId,
                        generation = ulong.Parse(initial.Reported.Generation, CultureInfo.InvariantCulture),
                        catalogVersion = "ollivander-private-party-v1", locale = "cs-CZ", consent = new { audio = false, image = false } })
                };
                create.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
                using var created = await http.SendAsync(create, cancellation);
                if (!created.IsSuccessStatusCode) { return new(false, "isolated-conversation-rejected"); }
                var conversation = await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellation);
                host.ConversationId = conversation.GetProperty("conversationId").GetString()!;
                host.ConversationBoot = initial.Source.BootId;
                host.ConversationGeneration = initial.Reported.Generation;
            }
            var conversationId = host.ConversationId;
            var textHex = Convert.ToHexStringLower(Encoding.UTF8.GetBytes(command.Text!));
            var prepared = await InternalAsync(host, $"INTEGRATION_PREPARE {command.OperationId} {conversationId} {clock.GetUtcNow().ToUnixTimeSeconds()} {textHex}");
            if (!prepared.Accepted || prepared.ExternalRequest is null) { return prepared with { ExternalRequest = null }; }
            using var submit = new HttpRequestMessage(HttpMethod.Post, $"v1/conversations/{conversationId}/turns")
            {
                Content = new StringContent(prepared.ExternalRequest.Value.GetProperty("body").GetRawText(), Encoding.UTF8, "application/json")
            };
            submit.Headers.Add("Idempotency-Key", command.OperationId);
            using var submitted = await http.SendAsync(submit, cancellation);
            if (!submitted.IsSuccessStatusCode) { return new(false, "isolated-turn-rejected"); }
            var operation = await submitted.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellation);
            var operationId = operation.GetProperty("operationId").GetString()!;
            var bound = await InternalAsync(host, "INTEGRATION_BIND " + operationId);
            if (!bound.Accepted) { return bound with { ExternalRequest = null }; }
            // Network polling uses wall-clock deadlines regardless of virtual time.
            var deadline = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(deadline).TotalSeconds < 20)
            {
                using var response = await http.GetAsync("v1/operations/" + operationId, HttpCompletionOption.ResponseHeadersRead, cancellation);
                if (!response.IsSuccessStatusCode) { return new(false, "isolated-poll-rejected"); }
                await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
                var buffer = new byte[4097];
                var count = 0;
                while (count < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(count), cancellation);
                    if (read == 0) { break; }
                    count += read;
                }
                if (count > 4096) { return new(false, "isolated-response-too-large"); }
                var bytes = buffer.AsSpan(0, count).ToArray();
                using var document = JsonDocument.Parse(bytes);
                var status = document.RootElement.GetProperty("status").GetString();
                if (status is not ("pending" or "running"))
                {
                    var result = await InternalAsync(host, $"INTEGRATION_RESULT {clock.GetUtcNow().ToUnixTimeSeconds()} {Convert.ToHexStringLower(bytes)}");
                    return result with { ExternalRequest = null };
                }
                await Task.Delay(100, cancellation);
            }
            return new(false, "isolated-network-deadline");
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or TaskCanceledException or TimeoutException or KeyNotFoundException)
        {
            return new(false, "isolated-backend-unavailable");
        }
    }
    private static async Task DrainAsync(StreamReader reader, CancellationToken cancellation)
    {
        while (await reader.ReadLineAsync(cancellation) is not null) { }
    }
}
