using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Kufr.Backend.Features.Conversations;

internal static class TranscriptionUploadReader
{
    internal const long MaxRequestBytes = 2_000_000;
    private const int MaxWavBytes = 1_920_512;

    public static async Task<UploadReadResult> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !string.Equals(mediaType.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return new UploadReadResult(400, null);
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > 70)
        {
            return new UploadReadResult(400, null);
        }

        var reader = new MultipartReader(boundary, request.Body)
        {
            BodyLengthLimit = MaxRequestBytes,
            HeadersCountLimit = 4,
            HeadersLengthLimit = 1024
        };
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        byte[]? wav = null;
        var partCount = 0;

        UploadReadResult Reject(int status)
        {
            if (wav is not null)
            {
                CryptographicOperations.ZeroMemory(wav);
            }

            return new UploadReadResult(status, null);
        }

        try
        {
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
            {
                partCount++;
                if (partCount > 5
                    || !ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                    || !string.Equals(disposition.DispositionType.Value, "form-data", StringComparison.OrdinalIgnoreCase))
                {
                    return Reject(400);
                }

                var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
                if (name == "wav")
                {
                    if (wav is not null
                        || !string.Equals(section.ContentType, "audio/wav", StringComparison.OrdinalIgnoreCase))
                    {
                        return Reject(400);
                    }

                    var content = await ReadBoundedAsync(section.Body, MaxWavBytes, cancellationToken);
                    if (content is null)
                    {
                        return Reject(413);
                    }

                    var wavStatus = ValidateWav(content);
                    if (wavStatus != 200)
                    {
                        CryptographicOperations.ZeroMemory(content);
                        return Reject(wavStatus);
                    }

                    wav = content;
                    continue;
                }

                if (name is not ("clientTurnId" or "bootId" or "generation" or "deviceRevision")
                    || fields.ContainsKey(name))
                {
                    return Reject(400);
                }

                var fieldBytes = await ReadBoundedAsync(section.Body, 64, cancellationToken);
                if (fieldBytes is null)
                {
                    return Reject(400);
                }

                fields.Add(name, Encoding.UTF8.GetString(fieldBytes));
            }
        }
        catch (InvalidDataException)
        {
            return Reject(400);
        }
        catch
        {
            if (wav is not null)
            {
                CryptographicOperations.ZeroMemory(wav);
            }

            throw;
        }

        if (wav is null
            || fields.Count != 4
            || !Guid.TryParseExact(fields["clientTurnId"], "D", out var clientTurnId)
            || clientTurnId == Guid.Empty
            || !Guid.TryParseExact(fields["bootId"], "D", out var bootId)
            || bootId == Guid.Empty
            || !int.TryParse(fields["generation"], NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
            || !int.TryParse(fields["deviceRevision"], NumberStyles.None, CultureInfo.InvariantCulture, out var revision))
        {
            return Reject(400);
        }

        return new UploadReadResult(200, new TranscriptionUpload(clientTurnId, bootId, generation, revision, wav));
    }

    private static async Task<byte[]?> ReadBoundedAsync(Stream body, int maximum, CancellationToken cancellationToken)
    {
        var content = new byte[maximum];
        var used = 0;
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var read = await body.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    return content.AsSpan(0, used).ToArray();
                }

                if (used + read > maximum)
                {
                    return null;
                }

                buffer.AsSpan(0, read).CopyTo(content.AsSpan(used));
                used += read;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static int ValidateWav(ReadOnlySpan<byte> content)
    {
        if (content.Length < 44
            || !content[..4].SequenceEqual("RIFF"u8)
            || !content[8..12].SequenceEqual("WAVE"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(content[4..8]) != content.Length - 8)
        {
            return 400;
        }

        var offset = 12;
        var hasFormat = false;
        var hasSamples = false;
        while (offset + 8 <= content.Length)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(content[(offset + 4)..(offset + 8)]);
            var end = (long)offset + 8 + size;
            if (end > content.Length)
            {
                return 400;
            }

            var data = content[(offset + 8)..(int)end];
            if (content[offset..(offset + 4)].SequenceEqual("fmt "u8))
            {
                if (hasFormat || data.Length < 16
                    || BinaryPrimitives.ReadUInt16LittleEndian(data[0..2]) != 1
                    || BinaryPrimitives.ReadUInt16LittleEndian(data[2..4]) != 1
                    || BinaryPrimitives.ReadUInt32LittleEndian(data[4..8]) != 32000
                    || BinaryPrimitives.ReadUInt32LittleEndian(data[8..12]) != 64000
                    || BinaryPrimitives.ReadUInt16LittleEndian(data[12..14]) != 2
                    || BinaryPrimitives.ReadUInt16LittleEndian(data[14..16]) != 16)
                {
                    return 400;
                }

                hasFormat = true;
            }
            else if (content[offset..(offset + 4)].SequenceEqual("data"u8))
            {
                if (size > 1_920_000)
                {
                    return 413;
                }

                if (hasSamples || size == 0 || size % 2 != 0)
                {
                    return 400;
                }

                hasSamples = true;
            }

            offset = (int)(end + (size % 2));
        }

        return hasFormat && hasSamples && offset == content.Length ? 200 : 400;
    }
}

internal sealed record UploadReadResult(int Status, TranscriptionUpload? Upload);
internal sealed record TranscriptionUpload(
    Guid ClientTurnId,
    Guid BootId,
    int Generation,
    int DeviceRevision,
    byte[] Wav);
