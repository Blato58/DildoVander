using System.Diagnostics;
using System.ClientModel;
using System.ClientModel.Primitives;
using OpenAI;
using OpenAI.Audio;
using Kufr.Backend.Features.OpenAi;
using System.Security.Cryptography;
using System.Text;

// Experimental surface of the pinned official SDK.
#pragma warning disable OPENAI001
namespace Kufr.Backend.Features.Speech;

internal sealed class OpenAiSpeechClient(
    HttpClient httpClient,
    string apiKey,
    ILogger<OpenAiSpeechClient>? logger = null)
{
    internal const string Model = "gpt-4o-mini-tts-2025-12-15";

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

        using var bounded = BoundedSdkTransport.Create(httpClient, PcmSpeechAudio.MaxProviderBytes,
            ["audio/pcm", "application/octet-stream"]);
        var client = new AudioClient(Model, new ApiKeyCredential(apiKey), new OpenAIClientOptions
        {
            Transport = BoundedSdkTransport.Pipeline(bounded),
            RetryPolicy = new ClientRetryPolicy(0)
        });
        var providerTimer = Stopwatch.StartNew();
        var response = await client.GenerateSpeechAsync(reply, new GeneratedSpeechVoice(options.Voice),
            new SpeechGenerationOptions
            {
                SpeedRatio = (float)options.Speed,
                ResponseFormat = GeneratedSpeechFormat.Pcm,
                Instructions = "Mluv česky svižně, přirozeně a srozumitelně jako sebevědomý kouzelný kufr. "
                    + "Hravý, lehce ironický tón, plynulé tempo bez dlouhých umělých pauz. Přečti přesně vstupní text, "
                    + "nepřidávej vlastní slova ani zvuky."
            }, cancellationToken);
        var pcm = response.Value.ToArray();
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

}

#pragma warning restore OPENAI001
