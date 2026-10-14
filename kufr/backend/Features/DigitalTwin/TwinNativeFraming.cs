using System.Text;
using System.Text.Json;

namespace Kufr.Backend.Features.DigitalTwin;

// Binary attachments bypass StreamReader: its prefetch buffer would consume PCM.
internal static class TwinNativeFraming
{
    internal static async Task<(JsonDocument Document, byte[] Attachment)> ReadAsync(Stream stream, CancellationToken cancellation)
    {
        using var line = new MemoryStream();
        var single = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(single, cancellation) == 0) { throw new EndOfStreamException("Native reply truncated."); }
            if (single[0] == 10) { break; }
            if (line.Length >= 65536) { throw new InvalidDataException("Native JSON limit."); }
            line.WriteByte(single[0]);
        }
        var document = JsonDocument.Parse(line.ToArray());
        try
        {
            var root = document.RootElement;
            var media = root.TryGetProperty("media", out var descriptor);
            var service = root.TryGetProperty("mediaServiceRequest", out var request);
            if (media && service) { throw new InvalidDataException("Multiple native attachments."); }
            if (!media && !service) { return (document, []); }
            if (service) { descriptor = request; }
            var length = descriptor.GetProperty("byteLength").GetInt32();
            var maximum = service && descriptor.GetProperty("kind").GetString() == "image-upload" ? 262144 : 32768;
            if (length < 0 || length > maximum) { throw new InvalidDataException("Native attachment limit."); }
            var bytes = new byte[length];
            await stream.ReadExactlyAsync(bytes, cancellation);
            if (await stream.ReadAsync(single, cancellation) != 1 || single[0] != 10)
            { throw new InvalidDataException("Native attachment terminator."); }
            return (document, bytes);
        }
        catch { document.Dispose(); throw; }
    }

    internal static async Task WriteAsync(Stream stream, string line, byte[]? attachment, CancellationToken cancellation)
    {
        if (line.Length > 131072 || line.Contains('\n') || line.Contains('\r'))
        { throw new InvalidDataException("Native command framing."); }
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), cancellation);
        if (attachment is not null)
        {
            if (attachment.Length > 65536) { throw new InvalidDataException("Native input limit."); }
            await stream.WriteAsync(attachment, cancellation);
            await stream.WriteAsync(new byte[] { 10 }, cancellation);
        }
        await stream.FlushAsync(cancellation);
    }
}
