using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Kufr.Backend.Features.Show;
using Kufr.Backend.Features.Speech;

internal static class ShowSpeechProviderTests
{
    internal static async Task RunAsync()
    {
        await RequestAndDeviceAudioContractAsync();
        await InvalidInputDoesNotDispatchAsync();
        await BoundedProviderResponsesAsync();
        await FailureDoesNotRetryAsync();
        await CancellationWipesTransportBufferAsync();
        Console.WriteLine("Show speech SDK transport tests passed: PCM/voice, bounded content, single dispatch and cancelled-buffer wiping (no network).");
    }

    private static async Task RequestAndDeviceAudioContractAsync()
    {
        const string line = "Ministerstvo uznává zásah.";
        var expectedOptions = SpeechOptions.Default;
        var handler = new Handler(async (request, cancellation) =>
        {
            Check(request.Method == HttpMethod.Post
                && request.RequestUri == new Uri("https://api.openai.com/v1/audio/speech"), "official SDK uses the HTTPS speech endpoint");
            Check(request.Headers.Authorization?.ToString() == "Bearer fixture-show-key", "credential stays in the authorization header");
            Check(request.Content!.Headers.ContentType?.MediaType == "application/json", "SDK sends JSON");
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellation));
            var payload = body.RootElement;
            Check(payload.GetProperty("model").GetString() == ShowCueSpeechClient.Model
                && payload.GetProperty("voice").GetString() == expectedOptions.Voice
                && Math.Abs(payload.GetProperty("speed").GetDouble() - expectedOptions.Speed) < .0001
                && payload.GetProperty("response_format").GetString() == "pcm"
                && payload.GetProperty("input").GetString() == line,
                "request preserves the pinned model, selected voice, speed, PCM and exact authored line");
            Check(payload.GetProperty("instructions").GetString()!.Contains("Přečti přesně vstupní text", StringComparison.Ordinal),
                "server instructions prohibit adding improvised words");
            return Response(Pcm(), expectedOptions.Voice == "marin" ? "audio/pcm" : "application/octet-stream");
        });
        using var http = new HttpClient(handler);
        var client = new ShowCueSpeechClient(http, "fixture-show-key");
        foreach (var options in new[] { SpeechOptions.Default, new SpeechOptions("cedar", 1.15) })
        {
            expectedOptions = options;
            var wav = await client.GenerateAsync(line, options, default);
            Check(wav.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                && wav.AsSpan(8, 4).SequenceEqual("WAVE"u8)
                && BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)) == 32_000
                && BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)) == 1
                && BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)) == 16
                && wav.Length == 44 + 640 * 2,
                "24 kHz provider PCM becomes a real 32 kHz mono PCM16 device WAV with preserved duration");
            Check(wav.AsSpan(44).ToArray().Any(value => value != 0), "resampling preserves nonempty audio rather than fabricating a silent header");
        }
        Check(handler.Calls == 2, "one provider request per authored line");
    }

    private static async Task InvalidInputDoesNotDispatchAsync()
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("Invalid input must not reach HTTP."));
        using var http = new HttpClient(handler);
        var client = new ShowCueSpeechClient(http, "fixture-show-key");
        foreach (var line in new[] { "", " \n\t", new string('a', 501), string.Concat(Enumerable.Repeat("🪄", 501)) })
        {
            await RejectAsync<ArgumentException>(() => client.GenerateAsync(line, default));
        }
        foreach (var options in new[] { new SpeechOptions("unapproved", 1), new SpeechOptions(null!, 1),
            new SpeechOptions("marin", double.NaN), new SpeechOptions("marin", .84), new SpeechOptions("marin", 1.26) })
        {
            await RejectAsync<ArgumentException>(() => client.GenerateAsync("Ahoj", options, default));
        }
        Check(handler.Calls == 0, "invalid short text, voice or speed cannot create a provider request");
    }

    private static async Task BoundedProviderResponsesAsync()
    {
        foreach (var contentType in new string?[] { "audio/mpeg", "application/json", null })
        {
            using var stream = new ObservedStream(128);
            var handler = new Handler((_, _) => Task.FromResult(StreamResponse(stream, contentType)));
            using var http = new HttpClient(handler);
            await RejectAsync<InvalidDataException>(() => new ShowCueSpeechClient(http, "fixture-show-key").GenerateAsync("Ahoj", default));
            Check(handler.Calls == 1 && stream.ReadCalls == 0 && stream.Disposed, "unexpected media type is rejected before reading and response is disposed");
        }
        using (var declared = new ObservedStream(128))
        {
            var handler = new Handler((_, _) => Task.FromResult(StreamResponse(declared, "audio/pcm", PcmSpeechAudio.MaxProviderBytes + 1L)));
            using var http = new HttpClient(handler);
            await RejectAsync<InvalidDataException>(() => new ShowCueSpeechClient(http, "fixture-show-key").GenerateAsync("Ahoj", default));
            Check(handler.Calls == 1 && declared.ReadCalls == 0 && declared.Disposed, "declared oversize is rejected without buffering audio");
        }
        using (var streamed = new ObservedStream(PcmSpeechAudio.MaxProviderBytes + 2))
        {
            var handler = new Handler((_, _) => Task.FromResult(StreamResponse(streamed, "application/octet-stream")));
            using var http = new HttpClient(handler);
            await RejectAsync<InvalidDataException>(() => new ShowCueSpeechClient(http, "fixture-show-key").GenerateAsync("Ahoj", default));
            Check(handler.Calls == 1 && streamed.BytesRead == PcmSpeechAudio.MaxProviderBytes + 1
                && streamed.SawNonzeroData && streamed.Buffer.Span.ToArray().All(value => value == 0) && streamed.Disposed,
                "unknown-length response stops at limit plus one and wipes the bounded transport buffer");
        }
        foreach (var pcm in new[] { Array.Empty<byte>(), new byte[3] })
        {
            var handler = new Handler((_, _) => Task.FromResult(Response(pcm, "audio/pcm")));
            using var http = new HttpClient(handler);
            await RejectAsync<InvalidDataException>(() => new ShowCueSpeechClient(http, "fixture-show-key").GenerateAsync("Ahoj", default));
            Check(handler.Calls == 1, "empty or misaligned PCM cannot be returned as a valid device WAV");
        }
    }

    private static async Task FailureDoesNotRetryAsync()
    {
        foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable, HttpStatusCode.TemporaryRedirect })
        {
            var handler = new Handler((_, _) =>
            {
                var response = new HttpResponseMessage(status) { Content = new StringContent("provider-private-error") };
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                if (status == HttpStatusCode.TemporaryRedirect) { response.Headers.Location = new Uri("https://untrusted.invalid/speech"); }
                return Task.FromResult(response);
            });
            using var http = new HttpClient(handler);
            await RejectAsync<InvalidDataException>(() => new ShowCueSpeechClient(http, "fixture-show-key").GenerateAsync("Ahoj", default));
            Check(handler.Calls == 1, "HTTP failure or redirect cannot trigger a second provider request");
        }
    }

    private static async Task CancellationWipesTransportBufferAsync()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new ObservedStream(256, cancelOnSecondRead: cancellation);
        var handler = new Handler((_, _) => Task.FromResult(StreamResponse(stream, "audio/pcm")));
        using var http = new HttpClient(handler);
        await RejectAsync<OperationCanceledException>(() => new ShowCueSpeechClient(http, "fixture-show-key").GenerateAsync("Ahoj", cancellation.Token));
        Check(handler.Calls == 1 && stream.ReadCalls == 2 && stream.SawNonzeroData
            && !stream.Buffer.IsEmpty && stream.Buffer.Span.ToArray().All(value => value == 0) && stream.Disposed,
            "cancellation after partial audio wipes the actually-used transport buffer and releases its stream");
    }

    private static byte[] Pcm()
    {
        var bytes = new byte[480 * 2];
        for (var sample = 0; sample < 480; sample++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(sample * 2), (short)(10_000 * Math.Sin(2 * Math.PI * 800 * sample / 24_000)));
        }
        return bytes;
    }
    private static HttpResponseMessage Response(byte[] bytes, string contentType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return response;
    }
    private static HttpResponseMessage StreamResponse(Stream stream, string? contentType, long? length = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = contentType is null ? null : new MediaTypeHeaderValue(contentType);
        response.Content.Headers.ContentLength = length;
        return response;
    }
    private static async Task RejectAsync<T>(Func<Task<byte[]>> action) where T : Exception
    {
        try { _ = await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Show speech test expected {typeof(T).Name}.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException($"Show speech test failed: {message}"); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }
    private sealed class ObservedStream(int length, CancellationTokenSource? cancelOnSecondRead = null) : Stream
    {
        internal int ReadCalls;
        internal int BytesRead;
        internal Memory<byte> Buffer { get; private set; }
        internal bool SawNonzeroData;
        internal bool Disposed;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            if (ReadCalls == 2 && cancelOnSecondRead is not null) { cancelOnSecondRead.Cancel(); }
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, length - BytesRead);
            if (cancelOnSecondRead is not null) { count = Math.Min(count, 64); }
            if (count == 0) { return ValueTask.FromResult(0); }
            buffer.Span[..count].Fill(0x5A);
            Buffer = buffer[..count];
            SawNonzeroData = true;
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
