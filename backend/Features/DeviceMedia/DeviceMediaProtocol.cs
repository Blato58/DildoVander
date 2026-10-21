using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Realtime;

namespace Kufr.Backend.Features.DeviceMedia;

// KF v1 framing is shared with local media. Stream authorization and the
// half-duplex session are negotiated separately by this device-only endpoint.
internal static class DeviceMediaProtocol
{
    internal const string Subprotocol = "kufr-device-media-v1";
    internal const int HeaderBytes = 16;
    internal const int MaxPcmBytes = 4096;
    internal const int MaxMessageBytes = 8192;

    internal static bool Fields(JsonElement value, params string[] names) =>
        value.ValueKind == JsonValueKind.Object && value.EnumerateObject()
            .Select(property => property.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(names.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    internal static (string Engine, byte[] Request) ReadOpening(ReadOnlyMemory<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (!Fields(root, "type", "engine", "request") || root.GetProperty("type").GetString() != "open"
            || root.GetProperty("engine").GetString() is not ("gpt-live" or "realtime")
            || root.GetProperty("request").ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("invalid_media_open");
        }
        var request = Encoding.UTF8.GetBytes(root.GetProperty("request").GetRawText());
        try
        {
            if (request.Length > 4096 || RealtimeProtocol.ReadRequest(request) is not { Opening: true } parsed
                || !RealtimeProtocol.IsValid(parsed))
            {
                throw new InvalidDataException("invalid_media_open");
            }
            return (root.GetProperty("engine").GetString()!, request);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(request);
            throw;
        }
    }

    internal static (uint Stream, uint Sequence) ReadPcmHeader(ReadOnlySpan<byte> message)
    {
        if (message.Length <= HeaderBytes || message.Length > HeaderBytes + MaxPcmBytes
            || (message.Length - HeaderBytes) % 2 != 0 || message[0] != 'K' || message[1] != 'F'
            || message[2] != 1 || message[3] != 1
            || BinaryPrimitives.ReadUInt32LittleEndian(message[12..]) != message.Length - HeaderBytes)
        {
            throw new InvalidDataException("invalid_media_frame");
        }
        var stream = BinaryPrimitives.ReadUInt32LittleEndian(message[4..]);
        if (stream == 0) { throw new InvalidDataException("invalid_media_frame"); }
        return (stream, BinaryPrimitives.ReadUInt32LittleEndian(message[8..]));
    }

    internal static void WritePcmHeader(Span<byte> message, uint stream, uint sequence, int count)
    {
        message[0] = (byte)'K'; message[1] = (byte)'F'; message[2] = 1; message[3] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(message[4..], stream);
        BinaryPrimitives.WriteUInt32LittleEndian(message[8..], sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(message[12..], checked((uint)count));
    }
}
