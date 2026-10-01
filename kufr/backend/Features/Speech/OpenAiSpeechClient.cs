using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Kufr.Backend.Features.Speech;

internal sealed class OpenAiSpeechClient(HttpClient httpClient, string apiKey)
{
    internal const string Model = "gpt-4o-mini-tts-2025-12-15";
    internal const string Voice = "marin";
    private static readonly Uri Endpoint = new("https://api.openai.com/v1/audio/speech");

    public async Task<byte[]> GenerateAsync(string reply, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reply) || reply.EnumerateRunes().Count() > 500)
        {
            throw new ArgumentException("Speech requires a validated short reply.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(new
            {
                model = Model,
                voice = Voice,
                input = reply,
                response_format = "pcm",
                instructions = "Mluv česky, přirozeně a srozumitelně jako sebevědomý kouzelný kufr. "
                    + "Hravý, lehce ironický tón; krátké pauzy u pointy. Přečti přesně vstupní text, "
                    + "nepřidávej vlastní slova ani zvuky."
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType is not ("audio/pcm" or "application/octet-stream"))
        {
            throw new InvalidDataException("Unexpected speech content type.");
        }

        var pcm = await ReadPcmAsync(response.Content, cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return PcmSpeechAudio.ToDeviceWav(pcm);
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
