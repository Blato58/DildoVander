using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Kufr.Backend.Features.TestingSessions;

// A private process transport only. Production C++ owners admit, journal and
// sequence effects; no provider text or decision is synthesized by this adapter.
internal sealed class TestingHardwareAdapter(IConfiguration configuration) : IDisposable
{
    private sealed class Host(string id, string device)
    {
        internal readonly string Id = id, Device = device;
        internal readonly SemaphoreSlim Gate = new(1);
        internal Process? Process;
        internal long ClockMs;
        internal readonly Stopwatch Clock = new();
        internal bool Disconnected, Camera = true, FailServo;
        internal string? PendingOperation;
        internal int? PendingCompartment;
        internal readonly List<TestingHardwareEvent> Events = [];
    }
    private readonly ConcurrentDictionary<string, Host> hosts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> retired = new(StringComparer.Ordinal);
    private readonly string root = (configuration["Backend:DatabasePath"] ?? throw new InvalidOperationException("Backend:DatabasePath missing.")) + ".testing/hardware";
    private readonly string executable = configuration["TestingSessions:HardwareExecutable"]
        ?? Path.Combine(AppContext.BaseDirectory, "testing-native", OperatingSystem.IsWindows() ? "kufr-native-twin.exe" : "kufr-native-twin");
    private bool disposed;

    private Host Get(string id, string device)
    {
        if (disposed) throw new ObjectDisposedException(nameof(TestingHardwareAdapter));
        if (id.Length != 32 || !id.All(char.IsAsciiHexDigit) || device != "test-" + id)
            throw new TestingException(409, "testing_hardware_identity_invalid");
        if (retired.ContainsKey(id)) return new Host(id, device) { Disconnected = true };
        if (hosts.Count >= 32 && !hosts.ContainsKey(id)) throw new TestingException(429, "testing_hardware_capacity");
        return hosts.GetOrAdd(id, _ => new Host(id, device));
    }
    internal async Task<TestingHardware> SnapshotAsync(string id, string virtualId, CancellationToken token)
    {
        var host = Get(id, virtualId);
        await host.Gate.WaitAsync(token);
        try
        {
            if (host.Disconnected) return Unavailable(host, "injected_disconnect");
            try { return await ObserveAsync(host, token); }
            catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
            { Kill(host); Event(host, "hardware_unavailable", "native_runtime_unavailable"); return Unavailable(host, "native_runtime_unavailable"); }
        }
        finally { host.Gate.Release(); }
    }
    internal async Task<TestingHardware> PresentAsync(string id, string virtualId, string operationId, int compartment, CancellationToken token)
    {
        if (!Guid.TryParseExact(operationId, "D", out var operation) || operation == Guid.Empty || compartment is < 1 or > 6)
            throw new TestingException(400, "testing_proposal_invalid");
        var host = Get(id, virtualId);
        await host.Gate.WaitAsync(token);
        try
        {
            if (host.Disconnected) throw new TestingException(409, "testing_hardware_disconnected");
            await ObserveAsync(host, token);
            var result = await SendAsync(host, $"ACCEPTED_PROPOSAL {operationId} {compartment} main", token);
            var ack = result.GetProperty("commandResult");
            var code = ack.GetProperty("code").GetString()!;
            Event(host, "presentation", code, compartment);
            if (!ack.GetProperty("accepted").GetBoolean() && code != "duplicate")
                throw new TestingException(409, "testing_hardware_" + code.Replace('-', '_'));
            if (code == "presentation-admitted") { host.PendingOperation = operationId; host.PendingCompartment = compartment; }
            return Project(host, result);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        { Kill(host); throw new TestingException(503, "testing_hardware_unavailable"); }
        finally { host.Gate.Release(); }
    }
    internal async Task<TestingHardware> CommandAsync(string id, string virtualId, string action, string? fault, int? slot, CancellationToken token)
    {
        var host = Get(id, virtualId);
        if (action == "duel")
        {
            if (!Guid.TryParseExact(fault, "D", out _)) throw new TestingException(400, "testing_effect_operation_invalid");
            var deadline = Stopwatch.StartNew();
            while (true)
            {
                var snapshot = await SnapshotAsync(id, virtualId, token);
                if (snapshot.State == "idle") break;
                if (!snapshot.Connected || !snapshot.Ready || deadline.Elapsed > TimeSpan.FromSeconds(12))
                    throw new TestingException(409, "testing_hardware_effect_not_ready");
                // Waiting never owns the process gate; Stop/recovery keeps priority.
                await Task.Delay(100, token);
            }
        }
        await host.Gate.WaitAsync(action == "shutdown" ? CancellationToken.None : token);
        try
        {
            if (retired.ContainsKey(id) && action != "shutdown") throw new TestingException(409, "testing_hardware_retired");
            if (action is "disconnect" or "shutdown")
            {
                if (action == "shutdown") retired.TryAdd(id, 0);
                try { if (host.Process is { HasExited: false }) await SendAsync(host, "STOP", token); }
                finally { Kill(host); host.Disconnected = true; }
                Event(host, action, "injected_" + action);
                if (action == "shutdown") hosts.TryRemove(new KeyValuePair<string, Host>(id, host));
                return Unavailable(host, action);
            }
            if (action is "connect" or "reconnect") { host.Disconnected = false; Event(host, "reconnection", "injected_reconnect"); }
            if (host.Disconnected) throw new TestingException(409, "testing_hardware_disconnected");
            if (action == "camera") { host.Camera = fault != "missing"; Event(host, "camera", host.Camera ? "available" : "injected_missing_camera"); }
            await ObserveAsync(host, token);
            var line = action switch
            {
                "stop" => "STOP",
                "recover" => "INSPECT",
                "duel" => "DANCE " + fault,
                "fault" when fault is "servo" or "led" or "journal" or "jam" or "none"
                    && (slot ?? 0) is >= 0 and <= 6 => $"FAULT {fault} {slot ?? 0}",
                "connect" or "reconnect" or "camera" => "SNAPSHOT",
                _ => throw new TestingException(400, "testing_hardware_command_invalid")
            };
            if (action == "recover") await SendAsync(host, "FAULT none 0", token);
            var receipt = action == "duel" ? Path.Combine(root, id, "effect-" + fault + ".json") : null;
            if (receipt is not null)
            {
                if (File.Exists(receipt))
                {
                    if (await File.ReadAllTextAsync(receipt, token) == "accepted") return await ObserveAsync(host, token);
                    throw new TestingException(409, "testing_effect_previous_outcome_unknown");
                }
                await File.WriteAllTextAsync(receipt, "pending", token);
            }
            var result = await SendAsync(host, line, token);
            var ack = result.GetProperty("commandResult");
            Event(host, action, ack.GetProperty("code").GetString()!);
            if (!ack.GetProperty("accepted").GetBoolean()) throw new TestingException(409, "testing_hardware_command_rejected");
            if (receipt is not null) await File.WriteAllTextAsync(receipt, "accepted", CancellationToken.None);
            if (action == "fault") host.FailServo = fault == "servo";
            if (action == "recover") { host.FailServo = false; host.Camera = true; }
            return Project(host, result);
        }
        finally { host.Gate.Release(); }
    }
    private async Task<TestingHardware> ObserveAsync(Host host, CancellationToken token)
    {
        if (retired.ContainsKey(host.Id)) { host.Disconnected = true; return Unavailable(host, "ended"); }
        if (host.Process is null || host.Process.HasExited)
        {
            if (!File.Exists(executable)) throw new IOException("Packaged testing hardware runtime unavailable.");
            var directory = Path.Combine(root, host.Id);
            Directory.CreateDirectory(directory);
            var info = new ProcessStartInfo(executable) { RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "--instance", host.Device, "--boot", Guid.NewGuid().ToString("D"),
                "--journal", Path.Combine(directory, "journal.bin"), "--build", "testing-native-v1" }) info.ArgumentList.Add(arg);
            host.Process = Process.Start(info) ?? throw new IOException("Native runtime failed to start.");
            host.Process.ErrorDataReceived += (_, _) => { }; // Never export process stderr or paths.
            host.Process.BeginErrorReadLine();
            host.ClockMs = 0; host.Clock.Restart();
            Event(host, "startup", "native_runtime_started");
            var initial = await SendAsync(host, "SNAPSHOT", token);
            // Fresh inventory is known only after the simulated inspection. A
            // recovered journal remains latched until explicit operator recovery.
            if (!File.Exists(Path.Combine(directory, "initialized")))
            {
                var inspected = await SendAsync(host, "INSPECT", token);
                if (!inspected.GetProperty("commandResult").GetProperty("accepted").GetBoolean())
                    throw new IOException("Initial virtual inspection rejected.");
                await File.WriteAllTextAsync(Path.Combine(directory, "initialized"), "simulation-only", token);
            }
        }
        var elapsed = host.Clock.ElapsedMilliseconds;
        while (host.ClockMs < elapsed)
        {
            host.ClockMs = Math.Min(elapsed, host.ClockMs + 60000);
            await SendAsync(host, "TICK " + host.ClockMs, token);
        }
        return Project(host, await SendAsync(host, "SNAPSHOT", token));
    }
    internal async Task DeleteAsync(string id, string virtualId, CancellationToken token)
    {
        var host = Get(id, virtualId);
        await host.Gate.WaitAsync(token);
        try
        {
            Kill(host);
            retired.TryAdd(id, 0);
            host.Disconnected = true;
            var directory = Path.Combine(root, id);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            hosts.TryRemove(new KeyValuePair<string, Host>(id, host));
        }
        finally { host.Gate.Release(); }
    }
    private static async Task<JsonElement> SendAsync(Host host, string line, CancellationToken token)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await host.Process!.StandardInput.WriteLineAsync(line.AsMemory(), bounded.Token);
            await host.Process.StandardInput.FlushAsync(bounded.Token);
            var response = await host.Process.StandardOutput.ReadLineAsync(bounded.Token);
            if (response is null || response.Length > 1_048_576) throw new IOException("Invalid native snapshot.");
            using var document = JsonDocument.Parse(response);
            return document.RootElement.Clone();
        }
        catch (OperationCanceledException) { Kill(host); if (token.IsCancellationRequested) throw; throw new TimeoutException(); }
        catch (JsonException) { Kill(host); throw new IOException("Invalid native snapshot."); }
    }
    private static TestingHardware Project(Host host, JsonElement snapshot)
    {
        var reported = snapshot.GetProperty("reported");
        var state = reported.GetProperty("state").GetString()!;
        var selected = reported.GetProperty("slot").GetInt32();
        var recovery = reported.GetProperty("needsRecovery").GetBoolean();
        // The observer's legacy phase vocabulary groups admission/completion as
        // idle. An owned slot still means a pending durable runtime operation.
        if (state == "idle" && selected != 0) state = "awaiting_runtime_completion";
        if (host.PendingOperation is { } operation)
        {
            if (state == "idle" && selected == 0 && !recovery)
            {
                host.Events.Add(new(Guid.NewGuid().ToString("N"), "presentation_completed", "virtual-hardware",
                    "completed_by_timing", DateTimeOffset.UtcNow, host.PendingCompartment, operation));
                host.PendingOperation = null;
            }
            else if (state == "recovery-required")
            {
                host.Events.Add(new(Guid.NewGuid().ToString("N"), "presentation_failed", "virtual-hardware",
                    "recovery_required", DateTimeOffset.UtcNow, host.PendingCompartment, operation));
                host.PendingOperation = null;
            }
        }
        var compartments = reported.GetProperty("servo").EnumerateArray().Select(servo =>
            new TestingCompartment(servo.GetProperty("slot").GetInt32(), servo.GetProperty("estimate").GetString()!)).ToArray();
        return new(true, state != "recovery-required", state, compartments, selected is >= 1 and <= 6 ? selected : null,
            state, "GRB " + reported.GetProperty("ledGrb").GetRawText(),
            reported.GetProperty("servo").GetRawText(), "mask " + reported.GetProperty("vibrationMask").GetInt32(),
            host.Camera, host.Events.TakeLast(100).ToArray(), host.FailServo);
    }
    private static TestingHardware Unavailable(Host host, string code) => new(false, false, code,
        Enumerable.Range(1, 6).Select(slot => new TestingCompartment(slot, "unknown")).ToArray(), null, code,
        "unknown", null, null, host.Camera, host.Events.TakeLast(100).ToArray(), host.FailServo);
    private static void Event(Host host, string type, string code, int? compartment = null)
    {
        host.Events.Add(new(Guid.NewGuid().ToString("N"), type, "virtual-hardware", code, DateTimeOffset.UtcNow, compartment));
        if (host.Events.Count > 100) host.Events.RemoveAt(0);
    }
    private static void Kill(Host host)
    {
        if (host.PendingOperation is { } operation)
        {
            host.Events.Add(new(Guid.NewGuid().ToString("N"), "presentation_failed", "virtual-hardware",
                "runtime_interrupted_outcome_unknown", DateTimeOffset.UtcNow, host.PendingCompartment, operation));
            host.PendingOperation = null;
        }
        if (host.Process is { } process) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } process.Dispose(); host.Process = null; }
    }
    public void Dispose() { disposed = true; foreach (var host in hosts.Values) Kill(host); }
}
