using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Conversations;

namespace Kufr.Backend.Features.VoiceTurns;

internal sealed class OpenAiTranscriptionClient(HttpClient httpClient, string apiKey)
{
    internal const string Model = "gpt-transcribe";
    private static readonly Uri Endpoint = new("https://api.openai.com/v1/audio/transcriptions");

    public async Task<string> TranscribeAsync(byte[] wav, CancellationToken cancellationToken)
    {
        if (wav.Length > 1_920_512 || TranscriptionUploadReader.ValidateWav(wav) != 200)
        {
            throw new InvalidDataException("Transcription requires bounded device WAV.");
        }

        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(Model), "model");
        multipart.Add(new StringContent("json"), "response_format");
        multipart.Add(new StringContent("cs"), "languages[]");
        var audio = new ByteArrayContent(wav);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        multipart.Add(audio, "file", "turn.wav");
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = multipart };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType != "application/json"
            || response.Content.Headers.ContentLength is > 16_384)
        {
            throw new InvalidDataException("Unexpected transcription response.");
        }

        var buffer = new byte[16_385];
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var used = 0;
            while (used < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(used), cancellationToken);
                if (count == 0)
                {
                    break;
                }

                used += count;
            }

            if (used == buffer.Length)
            {
                throw new InvalidDataException("Transcription response exceeds its limit.");
            }

            using var document = JsonDocument.Parse(buffer.AsMemory(0, used));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count(property => property.Name == "text") != 1
                || !root.TryGetProperty("text", out var text)
                || text.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("Missing transcription text.");
            }

            var transcript = text.GetString()!;
            if (string.IsNullOrWhiteSpace(transcript) || transcript.EnumerateRunes().Count() > 1000)
            {
                throw new InvalidDataException("Transcription text exceeds its limit.");
            }

            return transcript;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}
