using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Speech;

namespace Kufr.Backend.Features.Show;

internal sealed record ShowAmbientPreviewRequest(Guid OperationId);
internal sealed record ShowAmbientPreviewResponse(string Status, Guid OperationId, string LineId, string Text,
    bool Synthetic, string? WavBase64 = null, string? Code = null);
internal sealed record ShowAmbientPreviewResult(int StatusCode, ShowAmbientPreviewResponse Response);

// Phone-only authored audio. This feature never issues a show command or touches a device mailbox.
internal sealed class ShowAmbientPreview : IDisposable
{
    internal const int MaximumEntries = 16;
    internal const int MaximumWavBytes = 44 + 32_000 * 2 * 8;
    internal static readonly TimeSpan Retention = TimeSpan.FromSeconds(60);
    private readonly object gate = new();
    private readonly Dictionary<(string Device, Guid Operation), Entry> entries = new();
    private readonly OperationJournal journal;
    private readonly ShowSpeechProvider voice;
    private readonly TimeProvider clock;
    private readonly SpeechOptions speech;
    private readonly string mode;
    private readonly CancellationTokenSource lifetime;
    private readonly ITimer cleanup;
    private Task work = Task.CompletedTask;
    private bool generating;
    private bool disposed;

    public ShowAmbientPreview(IConfiguration configuration, OperationJournal journal, ShowSpeechProvider voice,
        TimeProvider clock)
    {
        this.journal = journal;
        this.voice = voice;
        this.clock = clock;
        mode = configuration["Show:DeviceCueSpeechMode"] ?? "configured";
        if (mode is not ("configured" or "fake" or "off"))
        { throw new InvalidOperationException("Unsupported Show:DeviceCueSpeechMode."); }
        speech = SpeechOptions.Resolve(configuration["Backend:Voice:Voice"], configuration.GetValue<double?>("Backend:Voice:Speed"));
        if (!SpeechOptions.IsValid(speech.Voice, speech.Speed))
        { throw new InvalidOperationException("Invalid configured ambient preview voice."); }
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(voice.Stopping);
        cleanup = clock.CreateTimer(_ => Sweep(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    internal ShowAmbientPreviewResult Start(string device, Guid operation)
    {
        lock (gate)
        {
            Sweep();
            var line = Line(operation);
            if (operation == Guid.Empty) { return Failed(operation, line, 400, "invalid_operation_id"); }
            if (entries.TryGetValue((device, operation), out var existing)) { return Result(existing); }
            // The existing journal retains request identities for 24 hours. A restart or expired
            // audio result consumes that identity; neither POST retries nor GET can regenerate it.
            var replay = journal.FindExisting(device, Route(device), operation, Hash(line));
            if (replay is not null)
            { return Failed(operation, line, replay.Conflict ? 409 : 410, replay.Conflict ? "preview_operation_mismatch" : "preview_expired"); }
            if (disposed || lifetime.IsCancellationRequested || mode == "off" || mode != "fake" && !voice.Enabled)
            { return Failed(operation, line, 503, "preview_unavailable"); }
            if (generating || entries.Count >= MaximumEntries)
            { return Failed(operation, line, 503, "preview_busy"); }
            // Preview concurrency is independent of the real show's speech slot, so
            // a phone preview can never delay device narration across devices.
            try
            {
                var reservation = journal.Reserve(device, Route(device), operation, Hash(line),
                    new(device, Guid.Empty, operation.ToString("D"), operation, 0, 0),
                    kind: "speech", consumesTurn: false,
                    estimatedCostMicroUsd: mode == "fake" ? 0 : ShowSpeechProvider.SpeechReservationMicroUsd);
                if (reservation.Existing || reservation.Conflict || reservation.OperationId.Length == 0)
                {
                    return Failed(operation, line, 409, "preview_operation_mismatch");
                }
                var entry = new Entry(operation, line, reservation.OperationId, clock.GetTimestamp());
                entries.Add((device, operation), entry);
                if (!journal.MarkRunning(entry.JournalOperation))
                { throw new InvalidOperationException("Preview speech claim is not dispatchable."); }
                generating = true;
                work = Task.Run(() => GenerateAsync(entry));
                return Result(entry);
            }
            catch
            {
                if (entries.TryGetValue((device, operation), out var entry))
                {
                    entry.Code = "preview_failed";
                    MarkFailed(entry);
                }
                return Failed(operation, line, 503, "preview_failed");
            }
        }
    }

    internal ShowAmbientPreviewResult Read(string device, Guid operation)
    {
        lock (gate)
        {
            Sweep();
            if (entries.TryGetValue((device, operation), out var entry)) { return Result(entry); }
            var line = Line(operation);
            var replay = journal.FindExisting(device, Route(device), operation, Hash(line));
            return Failed(operation, line, replay is null ? 404 : replay.Conflict ? 409 : 410,
                replay is null ? "preview_not_found" : replay.Conflict ? "preview_operation_mismatch" : "preview_expired");
        }
    }

    internal Task DrainAsync() { lock (gate) { return work; } }

    private async Task GenerateAsync(Entry entry)
    {
        byte[]? wav = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            wav = mode == "fake" ? PcmSpeechAudio.ToDeviceWav(new byte[4800])
                : await voice.SpeakAsync(entry.Line.Text, speech, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (!ValidWav(wav)) { throw new InvalidDataException("Invalid bounded preview WAV."); }
            lock (gate)
            {
                if (lifetime.IsCancellationRequested || Expired(entry)) { throw new OperationCanceledException(); }
                if (!journal.MarkCompleted(entry.JournalOperation))
                { throw new InvalidOperationException("Preview speech claim was revoked."); }
                entry.Audio = wav;
                wav = null;
            }
        }
        catch (Exception error)
        {
            lock (gate)
            {
                entry.Code = error is OperationCanceledException ? "preview_cancelled" : "preview_failed";
                MarkFailed(entry);
            }
        }
        finally
        {
            if (wav is not null) { CryptographicOperations.ZeroMemory(wav); }
            lock (gate) { generating = false; }
        }
    }

    private void MarkFailed(Entry entry)
    {
        try { journal.MarkFailed(entry.JournalOperation); }
        catch (Exception error) when (error is IOException or Microsoft.Data.Sqlite.SqliteException)
        { /* Keep the conservative reservation and consumed request identity. */ }
    }

    private void Sweep()
    {
        lock (gate)
        {
            foreach (var key in entries.Where(pair => Expired(pair.Value)).Select(pair => pair.Key).ToArray())
            {
                Clear(entries[key]);
                entries.Remove(key);
            }
        }
    }

    private bool Expired(Entry entry) => clock.GetElapsedTime(entry.Created) >= Retention;
    private string Hash(ShowLine line) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        ShowRepertoire.Version + "\n" + line.Id + "\n" + line.Text + "\n" + speech.Voice + "\n"
        + speech.Speed.ToString(CultureInfo.InvariantCulture) + "\n" + mode)));
    private static string Route(string device) => $"/v1/show/devices/{device}/ambient-previews";
    private static ShowLine Line(Guid operation) => ShowRepertoire.Ambient[
        (int)(BinaryPrimitives.ReadUInt32LittleEndian(operation.ToByteArray()) % (uint)ShowRepertoire.Ambient.Length)];
    private ShowAmbientPreviewResult Failed(Guid operation, ShowLine line, int status, string code) =>
        new(status, new("failed", operation, line.Id, line.Text, mode == "fake", Code: code));
    private ShowAmbientPreviewResult Result(Entry entry) => entry.Code is { } code
        ? Failed(entry.Operation, entry.Line, 200, code)
        : new(entry.Audio is null ? 202 : 200, new(entry.Audio is null ? "pending" : "ready", entry.Operation,
            entry.Line.Id, entry.Line.Text, mode == "fake", entry.Audio is null ? null : Convert.ToBase64String(entry.Audio)));
    private static void Clear(Entry entry)
    {
        if (entry.Audio is { } audio) { CryptographicOperations.ZeroMemory(audio); entry.Audio = null; }
    }

    private static bool ValidWav(byte[] wav) => wav.Length is >= 46 and <= MaximumWavBytes
        && wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) && wav.AsSpan(8, 8).SequenceEqual("WAVEfmt "u8)
        && BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)) == wav.Length - 8
        && BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(16)) == 16
        && BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20)) == 1
        && BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)) == 1
        && BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)) == 32000
        && BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(28)) == 64000
        && BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(32)) == 2
        && BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)) == 16
        && wav.AsSpan(36, 4).SequenceEqual("data"u8)
        && BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)) == wav.Length - 44 && (wav.Length - 44) % 2 == 0;

    public void Dispose()
    {
        Task pending;
        lock (gate)
        {
            if (disposed) { return; }
            disposed = true;
            lifetime.Cancel();
            cleanup.Dispose();
            foreach (var entry in entries.Values) { Clear(entry); }
            pending = work;
        }
        // Finish cancellation before DI disposes the provider and journal below us.
        try { pending.GetAwaiter().GetResult(); }
        finally { lifetime.Dispose(); }
    }

    private sealed class Entry(Guid operation, ShowLine line, string journalOperation, long created)
    {
        internal readonly Guid Operation = operation;
        internal readonly ShowLine Line = line;
        internal readonly string JournalOperation = journalOperation;
        internal readonly long Created = created;
        internal byte[]? Audio;
        internal string? Code;
    }
}
