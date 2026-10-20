using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using Kufr.Backend.Features.Realtime;

namespace Kufr.Backend.Features.DeviceMedia;

internal sealed class DeviceMediaEventSink(DeviceMediaSocket socket, CancellationTokenSource lifetime,
    Action<Guid> started) : ConversationEventSink
{
    private readonly Lock inputGate = new();
    private uint nextStream;
    private uint inputStream;
    private Guid inputId;
    private uint inputSequence;
    private uint retiredInputStream;
    private Guid retiredInputId;
    private uint retiredInputSequence;
    private uint outputStream;
    private uint outputSequence;
    private long outputGeneration;
    private bool hasStarted;
    private string? activeOperation;
    private string? pendingOperation;
    private long publishedGeneration;
    private CancellationClaim? cancellation;
    private bool closing;
    private string? engine;
    private RealtimeTurnRequest? opening;

    internal void BindOpening(string selectedEngine, RealtimeTurnRequest request)
    {
        if (engine is not null || hasStarted) { throw new InvalidOperationException("media_opening_already_bound"); }
        engine = selectedEngine;
        opening = request;
    }
    internal RealtimeTurnRequest? RealtimeOpening => engine == "realtime" ? opening : null;

    internal override bool HasStarted => hasStarted;
    internal override CancellationToken Disconnected => lifetime.Token;
    internal override void Start(Guid streamId) { started(streamId); hasStarted = true; }
    internal override void BindOperation(string operationId)
    {
        lock (inputGate)
        {
            ThrowIfClosing();
            if (cancellation is not null) { throw new InvalidOperationException("media_operation_transition_claimed"); }
            pendingOperation = operationId;
            activeOperation = null;
        }
    }
    internal bool HasPublishedOperation { get { lock (inputGate) { return activeOperation is not null; } } }
    internal bool MatchesOperation(string operation, long generation)
    {
        lock (inputGate) { return activeOperation == operation && publishedGeneration == generation; }
    }

    // These transport gates never include store I/O or a socket send. A claim
    // settles before the receiver initiates teardown or any acknowledgment.
    internal sealed class CancellationClaim(DeviceMediaEventSink owner, string operation, long generation)
    {
        internal DeviceMediaEventSink Owner { get; } = owner;
        internal string Operation { get; } = operation;
        internal long Generation { get; } = generation;
        internal TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal CancellationClaim? TryClaimCancellation(string operation, long generation, Func<long> currentGeneration)
    {
        lock (inputGate)
        {
            if (closing || lifetime.IsCancellationRequested || cancellation is not null || activeOperation != operation
                || publishedGeneration != generation || currentGeneration() != generation)
            {
                return null;
            }
            cancellation = new(this, operation, generation);
            activeOperation = null;
            return cancellation;
        }
    }

    internal void CompleteCancellation(CancellationClaim claim, bool close)
    {
        lock (inputGate)
        {
            if (claim.Owner != this || cancellation != claim)
            {
                throw new InvalidOperationException("media_cancel_claim_mismatch");
            }
            closing |= close;
            if (!closing)
            {
                activeOperation = claim.Operation;
                publishedGeneration = claim.Generation;
            }
            cancellation = null;
            claim.Settled.SetResult();
        }
    }

    internal override async Task BeforeNextOperationAsync(CancellationToken token)
    {
        while (true)
        {
            Task wait;
            lock (inputGate)
            {
                token.ThrowIfCancellationRequested();
                ThrowIfClosing();
                if (cancellation is null)
                {
                    // Retire A BEFORE the core can admit B. Publication gating
                    // alone would leave a new journal row exposed to A's Stop.
                    activeOperation = pendingOperation = null;
                    return;
                }
                wait = cancellation.Settled.Task;
            }
            await wait.WaitAsync(token);
        }
    }

    internal override async Task<long> AdvanceOutputAsync(Func<long> transition, CancellationToken token)
    {
        while (true)
        {
            Task wait;
            lock (inputGate)
            {
                token.ThrowIfCancellationRequested();
                ThrowIfClosing();
                if (cancellation is null)
                {
                    activeOperation = null;
                    return transition();
                }
                wait = cancellation.Settled.Task;
            }
            await wait.WaitAsync(token);
        }
    }

    private void ThrowIfClosing()
    {
        if (closing || lifetime.IsCancellationRequested) { throw new OperationCanceledException(lifetime.Token); }
    }

    private async Task WaitForCancellationAsync(CancellationToken token)
    {
        while (true)
        {
            Task? wait;
            lock (inputGate)
            {
                token.ThrowIfCancellationRequested();
                ThrowIfClosing();
                wait = cancellation?.Settled.Task;
            }
            if (wait is null) { return; }
            await wait.WaitAsync(token);
        }
    }

    private uint AllocateStream()
    {
        if (nextStream == uint.MaxValue) { throw new InvalidDataException("media_stream_exhausted"); }
        return ++nextStream;
    }

    internal (Guid Input, int Sequence) AdmitInput(uint stream, uint sequence)
    {
        lock (inputGate)
        {
            // One older input can still have its single credited frame in
            // flight when the next listener is published. Preserve that exact
            // envelope: the session discards ended input, never relabels it.
            if (stream != 0 && stream == retiredInputStream && sequence == retiredInputSequence && sequence <= int.MaxValue)
            {
                retiredInputSequence++;
                return (retiredInputId, checked((int)sequence));
            }
            if (inputStream == 0 || stream != inputStream || sequence != inputSequence || sequence > int.MaxValue)
            {
                throw new InvalidDataException("media_input_mismatch");
            }
            inputSequence++;
            return (inputId, checked((int)sequence));
        }
    }

    internal override async Task WriteAsync(object item, CancellationToken token)
    {
        if (item is ConversationOperationBinding binding)
        {
            await WaitForCancellationAsync(token);
            lock (inputGate)
            {
                if (pendingOperation != binding.OperationId) { throw new OperationCanceledException(token); }
                if (activeOperation == binding.OperationId && publishedGeneration == binding.ResponseGeneration) { return; }
            }
            await socket.SendJsonAsync(new { type = "media.operation", operationId = binding.OperationId,
                responseGeneration = binding.ResponseGeneration }, token);
            await WaitForCancellationAsync(token);
            token.ThrowIfCancellationRequested();
            lock (inputGate)
            {
                if (pendingOperation != binding.OperationId || lifetime.IsCancellationRequested)
                {
                    throw new OperationCanceledException(token);
                }
                activeOperation = binding.OperationId;
                publishedGeneration = binding.ResponseGeneration;
            }
            return;
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(item, RealtimeProtocol.JsonOptions);
        try
        {
            if (bytes.Length > 65536) { throw new InvalidDataException("media_event_too_large"); }
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString();
            string? cancelOperation = null;
            if (type == "listening")
            {
                uint stream;
                Guid input;
                bool changed;
                lock (inputGate)
                {
                    input = root.GetProperty("inputId").GetGuid();
                    changed = inputStream == 0 || input != inputId;
                    if (changed)
                    {
                        retiredInputStream = inputStream;
                        retiredInputId = inputId;
                        retiredInputSequence = inputSequence;
                        inputId = input;
                        inputStream = AllocateStream();
                        inputSequence = 0;
                    }
                    stream = inputStream;
                }
                if (changed)
                {
                    await socket.SendJsonAsync(new { type = "media.input", stream, inputId = input,
                        format = "pcm16-mono-24000", maxFrameBytes = DeviceMediaProtocol.MaxPcmBytes, credit = 1 }, token);
                }
            }
            if (type == "audio_cancel")
            {
                outputStream = 0;
                lock (inputGate) { cancelOperation = pendingOperation; activeOperation = null; }
            }
            if (type != "audio")
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, token);
                if (type == "audio_cancel")
                {
                    if (cancelOperation is not null)
                    {
                        await WriteAsync(new ConversationOperationBinding(cancelOperation,
                            root.GetProperty("responseGeneration").GetInt64()), token);
                    }
                }
                if (type is "closed" or "failed")
                {
                    lock (inputGate) { activeOperation = pendingOperation = null; }
                }
                return;
            }
            var generation = root.GetProperty("responseGeneration").GetInt64();
            var pcm = Convert.FromBase64String(root.GetProperty("delta").GetString()!);
            try
            {
                if (pcm.Length is 0 or > RealtimeProtocol.MaxChunkBytes || pcm.Length % 2 != 0)
                {
                    throw new InvalidDataException("invalid_provider_pcm");
                }
                if (outputStream == 0 || generation != outputGeneration)
                {
                    lock (inputGate) { outputStream = AllocateStream(); }
                    outputSequence = 0;
                    outputGeneration = generation;
                    await socket.SendJsonAsync(new { type = "media.output", stream = outputStream,
                        responseGeneration = generation, format = "pcm16-mono-24000" }, token);
                }
                var frame = new byte[DeviceMediaProtocol.HeaderBytes + DeviceMediaProtocol.MaxPcmBytes];
                try
                {
                    for (var offset = 0; offset < pcm.Length; offset += DeviceMediaProtocol.MaxPcmBytes)
                    {
                        if (outputSequence == uint.MaxValue) { throw new InvalidDataException("media_sequence_exhausted"); }
                        var count = Math.Min(DeviceMediaProtocol.MaxPcmBytes, pcm.Length - offset);
                        DeviceMediaProtocol.WritePcmHeader(frame, outputStream, outputSequence++, count);
                        pcm.AsSpan(offset, count).CopyTo(frame.AsSpan(DeviceMediaProtocol.HeaderBytes));
                        await socket.SendAsync(frame.AsMemory(0, DeviceMediaProtocol.HeaderBytes + count), WebSocketMessageType.Binary, token);
                    }
                }
                finally { CryptographicOperations.ZeroMemory(frame); }
            }
            finally { CryptographicOperations.ZeroMemory(pcm); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal override Task ProblemAsync(int status, string code) =>
        socket.SendJsonAsync(new { type = "failed", status, code }, lifetime.Token);
    internal override void Abort() => socket.Abort();
}
