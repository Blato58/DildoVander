using System.Net.Http.Headers;
using System.ClientModel;
using System.ClientModel.Primitives;
using OpenAI;
using OpenAI.Audio;
using Kufr.Backend.Features.OpenAi;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Conversations;

namespace Kufr.Backend.Features.VoiceTurns;

internal sealed class OpenAiTranscriptionClient(HttpClient httpClient, string apiKey)
{
    internal const string Model = "gpt-transcribe";

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
        using var bounded = BoundedSdkTransport.Create(httpClient, 16_384, ["application/json"]);
        var client = new AudioClient(Model, new ApiKeyCredential(apiKey), new OpenAIClientOptions
        {
            Transport = BoundedSdkTransport.Pipeline(bounded),
            RetryPolicy = new ClientRetryPolicy(0)
        });
        // GPT-Transcribe uses languages[]; the pinned typed options expose only
        // the legacy language field, so send it through the SDK protocol method.
        await using var upload = await multipart.ReadAsStreamAsync(cancellationToken);
        using var content = BinaryContent.Create(upload);
        var response = await client.TranscribeAudioAsync(content, multipart.Headers.ContentType!.ToString(),
            new RequestOptions { CancellationToken = cancellationToken });
        using var document = JsonDocument.Parse(response.GetRawResponse().Content.ToMemory());
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
}
