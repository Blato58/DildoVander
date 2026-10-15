using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Kufr.Backend.Features.DigitalTwin;

internal sealed partial class TwinMediaSessions
{
    private async Task PumpAsync(Actor actor)
    {
        using var slots = new SemaphoreSlim(4, 4);
        var tasks = new List<Task>();
        try
        {
            await foreach (var reply in actor.Services.Reader.ReadAllAsync(actor.Lifetime.Token))
            {
                await slots.WaitAsync(actor.Lifetime.Token);
                tasks.RemoveAll(task => task.IsCompleted);
                tasks.Add(DispatchAsync(reply));
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
            if (!actor.Cancelled && actor.State?.Active == true) { await CancelActorAsync(actor); }
        }
        async Task DispatchAsync(TwinPrivateReply reply)
        {
            try { await ServiceAsync(actor, reply); }
            catch (OperationCanceledException) when (actor.Lifetime.IsCancellationRequested) { }
            catch (Exception)
            {
                // Never abandon a failed service task while native is still
                // waiting for it. Technical status contains no upstream payload.
                InvalidateActor(actor, "media_service_failed");
                try { await CleanupActorAsync(actor); }
                catch (Exception) { /* Actor is already fenced; cleanup is best effort. */ }
            }
            finally { slots.Release(); }
        }
    }
    private async Task ServiceAsync(Actor actor, TwinPrivateReply reply)
    {
        var request = reply.Service!;
        var bytes = reply.Attachment ?? [];
        var streamAdmitted = false;
        try
        {
            if (!ValidService(actor, request, bytes))
            { await ResultAsync(actor, request.RequestId, 400, "application/json", []); return; }
            if (actor.Settings.Engine == "fake" && request.Kind.StartsWith("realtime-", StringComparison.Ordinal))
            { await FakeServiceAsync(actor, request, bytes); return; }
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(actor.Lifetime.Token);
            lifetime.CancelAfter(request.Kind == "realtime-start" ? TimeSpan.FromMinutes(8) : TimeSpan.FromSeconds(4));
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Path.TrimStart('/'));
            if (request.Method == "POST")
            {
                message.Content = new ByteArrayContent(bytes);
                message.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(request.ContentType);
            }
            if (!string.IsNullOrEmpty(request.IdempotencyKey)) { message.Headers.Add("Idempotency-Key", request.IdempotencyKey); }
            foreach (var header in request.Headers) { message.Headers.Add(header.Key, header.Value); }
            using var response = await actor.Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
            if (request.Kind == "realtime-start" && response.IsSuccessStatusCode
                && response.Content.Headers.ContentType?.MediaType == "application/x-ndjson")
            {
                streamAdmitted = true;
                await ResultAsync(actor, request.RequestId, (int)response.StatusCode, "application/x-ndjson", []);
                await StreamEventsAsync(actor, response, lifetime.Token);
                return;
            }
            var body = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(lifetime.Token), 65536, lifetime.Token);
            if (response.IsSuccessStatusCode) { BindResponse(actor, request, body); }
            await ResultAsync(actor, request.RequestId, (int)response.StatusCode, "application/json", body);
            Array.Clear(body);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException
            or InvalidOperationException or TimeoutException or FormatException)
        {
            if (!actor.Cancelled)
            {
                if (streamAdmitted)
                {
                    // The initial 200 was already handed to native. Its request
                    // ID cannot receive a second result; retire this stream now.
                    InvalidateActor(actor, "media_stream_failed");
                    await CleanupActorAsync(actor);
                }
                else
                {
                    try { await ResultAsync(actor, request.RequestId, 503, "application/json", []); }
                    catch (TimeoutException) { }
                }
            }
        }
        finally { Array.Clear(bytes); }
    }
    private static bool ValidService(Actor actor, TwinMediaServiceRequest request, byte[] bytes)
    {
        if (!Guid.TryParseExact(request.RequestId, "D", out _) || request.ByteLength != bytes.Length
            || request.Path.Length > 240 || request.Path.Contains('?') || request.Path.Contains('%')
            || request.Headers.Count > 6 || request.Headers.Any(header => header.Value.Length > 128
                || header.Value.Any(char.IsControl) || header.Key is not ("Input-Id" or "Input-Sequence" or "Client-Turn-Id"
                    or "Boot-Id" or "Generation" or "Device-Revision" or "Catalog-Version"))) { return false; }
        if (request.IdempotencyKey.Length > 0 && !Guid.TryParseExact(request.IdempotencyKey, "D", out _)) { return false; }
        if (request.Kind == "conversation-create")
        {
            if (request.Method != "POST" || request.Path != "/v1/conversations" || request.ContentType != "application/json" || bytes.Length > 4096) { return false; }
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.GetProperty("deviceId").GetString() == actor.InstanceId
                && document.RootElement.GetProperty("bootId").GetString() == actor.BootId;
        }
        if (request.Kind == "image-poll")
        {
            lock (actor.Gate) { return request.Method == "GET" && bytes.Length == 0
                && request.Path.StartsWith("/v1/operations/", StringComparison.Ordinal)
                && actor.Operations.Contains(request.Path["/v1/operations/".Length..]); }
        }
        var prefix = "/v1/conversations/" + actor.ConversationId;
        if (actor.ConversationId is null) { return false; }
        return request.Kind switch
        {
            "conversation-delete" => request.Method == "DELETE" && request.Path == prefix && bytes.Length == 0,
            "image-upload" => request.Method == "POST" && request.Path == prefix + "/image" && bytes.Length is > 0 and <= 262144 && request.ContentType == "image/jpeg",
            "realtime-start" => request.Method == "POST" && request.Path == prefix + (actor.Settings.Engine == "gpt-live" ? "/gpt-live" : "/realtime-live") && bytes.Length <= 4096 && request.ContentType == "application/json",
            "realtime-input" or "realtime-played" or "realtime-playback" => request.Method == "POST" && actor.StreamId is not null
                && request.Path == prefix + (actor.Settings.Engine == "gpt-live" ? "/gpt-live/" : "/realtime-live/") + actor.StreamId
                    + (request.Kind == "realtime-input" ? "/audio" : request.Kind == "realtime-played" ? "/played" : "/playback")
                && bytes.Length <= 32768 && (request.Kind != "realtime-input" || bytes.Length > 0 && bytes.Length % 2 == 0),
            _ => false
        };
    }
    private static void BindResponse(Actor actor, TwinMediaServiceRequest request, byte[] body)
    {
        if (request.Kind is not ("conversation-create" or "image-upload")) { return; }
        using var json = JsonDocument.Parse(body);
        var value = json.RootElement.GetProperty(request.Kind == "conversation-create" ? "conversationId" : "operationId").GetString();
        if (!Guid.TryParseExact(value, "D", out _)) { throw new InvalidDataException("Invalid isolated service identity."); }
        lock (actor.Gate)
        {
            if (request.Kind == "conversation-create") { actor.ConversationId = value; }
            else { actor.Operations.Add(value!); }
        }
    }
    private async Task StreamEventsAsync(Actor actor, HttpResponseMessage response, CancellationToken cancellation)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        // Byte-bounded NDJSON reader prevents an unbounded StreamReader line.
        using var line = new MemoryStream();
        var buffer = new byte[4096];
        var total = 0;
        var closed = false;
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellation);
            if (count == 0) { break; }
            total += count;
            if (total > 20000000) { throw new InvalidDataException("Media stream byte limit."); }
            for (var index = 0; index < count; index++)
            {
                if (buffer[index] == 10)
                {
                    var bytes = line.ToArray(); line.SetLength(0);
                    if (bytes.Length > 0) { closed |= await EventAsync(actor, bytes); }
                    Array.Clear(bytes);
                }
                else
                {
                    if (line.Length >= 65536) { throw new InvalidDataException("Media NDJSON limit."); }
                    line.WriteByte(buffer[index]);
                }
            }
        }
        if (line.Length != 0) { throw new InvalidDataException("Truncated media event."); }
        if (!closed) { throw new InvalidDataException("Media stream ended without terminal event."); }
    }
    private async Task<bool> EventAsync(Actor actor, byte[] bytes)
    {
        if (actor.Cancelled || actor.State is null) { return false; }
        using var json = JsonDocument.Parse(bytes);
        if (json.RootElement.ValueKind != JsonValueKind.Object
            || !json.RootElement.TryGetProperty("type", out var typeValue)
            || typeValue.ValueKind != JsonValueKind.String)
        { throw new InvalidDataException("Invalid media event type."); }
        var type = typeValue.GetString();
        if (type == "session")
        {
            if (!json.RootElement.TryGetProperty("streamId", out var streamValue)
                || streamValue.ValueKind != JsonValueKind.String)
            { throw new InvalidDataException("Invalid stream identity."); }
            var stream = streamValue.GetString();
            if (!Guid.TryParseExact(stream, "D", out _)) { throw new InvalidDataException("Invalid stream identity."); }
            actor.StreamId = stream;
        }
        var command = $"MEDIA_EVENT {actor.SessionId} {actor.State.Epoch} {bytes.Length}";
        await DeliverEventAsync(() => hosts.MediaCommandAsync(actor.InstanceId, command, bytes), actor.Lifetime.Token);
        return type == "closed";
    }
    internal static async Task DeliverEventAsync(Func<Task<TwinCommandResult>> send, CancellationToken cancellation)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var remaining = TimeSpan.FromSeconds(5) - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) { throw new TimeoutException("Native media event capacity deadline."); }
            // A timeout has an uncertain native outcome, so it is never retried.
            var result = await send().WaitAsync(remaining, cancellation);
            if (result.Accepted) { return; }
            // Only a capacity rejection guarantees the event was not queued.
            // The wait belongs to the media stream reader, never the control loop.
            if (result.Code != "media-event-backpressure")
            { throw new InvalidDataException("Native media event rejected."); }
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(5))
            { throw new TimeoutException("Native media event capacity deadline."); }
            await Task.Delay(10, cancellation);
        }
    }
    private Task ResultAsync(Actor actor, string requestId, int status, string contentType, byte[] bytes) =>
        hosts.MediaCommandAsync(actor.InstanceId, $"MEDIA_RESULT {requestId} {status} {contentType} {bytes.Length}", bytes);
    private static async Task CloseCancelledAsync(Actor actor)
    {
        string conversation;
        lock (actor.Gate)
        {
            if (actor.ConversationId is null || actor.ClosingConversationId == actor.ConversationId) { return; }
            conversation = actor.ConversationId;
            actor.ClosingConversationId = conversation;
        }
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var request = new HttpRequestMessage(HttpMethod.Delete, "v1/conversations/" + conversation);
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
            using var response = await actor.Http.SendAsync(request, deadline.Token);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or ObjectDisposedException) { }
    }
    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken cancellation)
    {
        using var output = new MemoryStream();
        var buffer = new byte[Math.Min(8192, maximum + 1)];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellation);
            if (count == 0) { return output.ToArray(); }
            if (output.Length + count > maximum) { throw new InvalidDataException("Media body limit."); }
            output.Write(buffer, 0, count);
        }
    }
}
