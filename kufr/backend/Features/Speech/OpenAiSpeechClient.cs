using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Kufr.Backend.Features.Speech;

internal sealed class OpenAiSpeechClient(
    HttpClient httpClient,
    string apiKey,
    ILogger<OpenAiSpeechClient>? logger = null)
{
    internal const string Model = "gpt-4o-mini-tts-2025-12-15";
    private static readonly Uri Endpoint = new("https://api.openai.com/v1/audio/speech");

    public Task<byte[]> GenerateAsync(string reply, CancellationToken cancellationToken) =>
        GenerateAsync(reply, SpeechOptions.Default, cancellationToken);

    public async Task<byte[]> GenerateAsync(
        string reply,
        SpeechOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reply) || reply.EnumerateRunes().Count() > 500)
        {
            throw new ArgumentException("Speech requires a validated short reply.");
        }

        if (options.Voice is null || !SpeechOptions.IsValid(options.Voice, options.Speed))
        {
            throw new ArgumentException("Speech options are invalid.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(new
            {
                model = Model,
                voice = options.Voice,
                speed = options.Speed,
                input = reply,
                response_format = "pcm",
                instructions = "Mluv česky svižně, přirozeně a srozumitelně jako sebevědomý kouzelný kufr. "
                    + "Hravý, lehce ironický tón, plynulé tempo bez dlouhých umělých pauz. Přečti přesně vstupní text, "
                    + "nepřidávej vlastní slova ani zvuky."
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var providerTimer = Stopwatch.StartNew();
        using var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType is not ("audio/pcm" or "application/octet-stream"))
        {
            throw new InvalidDataException("Unexpected speech content type.");
        }

        var pcm = await ReadPcmAsync(response.Content, cancellationToken);
        providerTimer.Stop();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var conversionTimer = Stopwatch.StartNew();
            var wav = PcmSpeechAudio.ToDeviceWav(pcm);
            conversionTimer.Stop();
            logger?.LogInformation(
                "Speech completed: providerMs={ProviderMs}, conversionMs={ConversionMs}, outputBytes={OutputBytes}.",
                providerTimer.ElapsedMilliseconds,
                conversionTimer.ElapsedMilliseconds,
                wav.Length);
            return wav;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pcm);
        }
    }

    private static async Task<byte[]> ReadPcmAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > PcmSpeechAudio.MaxProviderBytes)
        {
            throw new InvalidDataException("Speech exceeds its duration limit.");
        }

        var buffer = new byte[PcmSpeechAudio.MaxProviderBytes + 1];
        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            var used = 0;
            while (used < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(used), cancellationToken);
                if (count == 0)
                {
                    return buffer.AsSpan(0, used).ToArray();
                }

                used += count;
            }

            throw new InvalidDataException("Speech exceeds its duration limit.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}
