using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Speech;
using Kufr.Backend.Features.VoiceTurns;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

internal static class VoiceProviderTests
{
    internal static async Task RunAsync()
    {
        CheckResampling();
        CheckConfiguration();
        await CheckProviderContractsAsync();
        await CheckRejectedProviderResponsesAsync();
        await CheckRuntimeAsync();
        Console.WriteLine("Voice provider, resampling and runtime tests passed.");
    }

    private static void CheckResampling()
    {
        var pcm = Tone(24_000, 1_000, 24_000);
        var wav = PcmSpeechAudio.ToDeviceWav(pcm);
        Require(wav.Length == 64_044 && BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)) == 32_000,
            "one second stays one second in 32 kHz WAV");
        Require(TranscriptionUploadReader.ValidateWav(wav) == 200, "output matches the device WAV contract");
        var squaredError = 0.0;
        for (var index = 40; index < 31_960; index++)
        {
            var actual = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + index * 2));
            var expected = 10_000 * Math.Sin(2 * Math.PI * 1_000 * index / 32_000);
            squaredError += Math.Pow(actual - expected, 2);
        }

        Require(Math.Sqrt(squaredError / 31_920) < 4, "resampling preserves pitch and sine waveform");
        var highFrequency = PcmSpeechAudio.ToDeviceWav(Tone(24_000, 10_000, 24_000));
        var imageAmplitude = 0.0;
        var fundamentalAmplitude = 0.0;
        for (var index = 40; index < 31_960; index++)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(highFrequency.AsSpan(44 + index * 2));
            imageAmplitude += sample * Math.Sin(2 * Math.PI * 14_000 * index / 32_000);
            fundamentalAmplitude += sample * Math.Sin(2 * Math.PI * 10_000 * index / 32_000);
        }

        Require(Math.Abs(imageAmplitude / fundamentalAmplitude) < 0.01,
            "upsampling suppresses the 14 kHz spectral image");
        foreach (var invalid in new[] { Array.Empty<byte>(), new byte[3], new byte[PcmSpeechAudio.MaxProviderBytes + 2] })
        {
            Reject<InvalidDataException>(() => PcmSpeechAudio.ToDeviceWav(invalid));
        }

        var dc = new byte[200];
        for (var index = 0; index < dc.Length; index += 2)
        {
            BinaryPrimitives.WriteInt16LittleEndian(dc.AsSpan(index), short.MaxValue);
        }

        var constant = PcmSpeechAudio.ToDeviceWav(dc);
        for (var index = 44; index < constant.Length; index += 2)
        {
            Require(BinaryPrimitives.ReadInt16LittleEndian(constant.AsSpan(index)) == short.MaxValue,
                "constant signal and endpoints remain bounded");
        }
    }

    private static void CheckConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        using var fake = new VoiceProviderRunner(configuration, new Lifetime());
        Require(!fake.Enabled, "voice is keyless fake by default");
        configuration["Backend:Voice:Mode"] = "openai";
        Reject<InvalidOperationException>(() => new VoiceProviderRunner(configuration, new Lifetime()));
        configuration["OPENAI_API_KEY"] = "test-only";
        using var defaultBudget = new VoiceProviderRunner(configuration, new Lifetime());
        Require(defaultBudget.Enabled, "provider key uses the approved default 3 USD session cap");
        foreach (var cap in new[] { "Session", "Daily", "Monthly" })
        {
            configuration[$"Backend:Usage:{cap}CapMicroUsd"] = "1000000";
        }

        using var real = new VoiceProviderRunner(configuration, new Lifetime());
        Require(real.Enabled, "explicit audio mode requires key and a session cap");
        configuration["Backend:Voice:Mode"] = "realtime";
        Reject<InvalidOperationException>(() => new VoiceProviderRunner(configuration, new Lifetime()));
    }

    private static async Task CheckProviderContractsAsync()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(async (request, cancellation) =>
        {
            calls++;
            Require(request.Headers.Authorization?.ToString() == "Bearer test-only", "key is in the header");
            if (request.RequestUri!.AbsolutePath.EndsWith("/speech", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
                Require(body.RootElement.GetProperty("model").GetString() == OpenAiSpeechClient.Model,
                    "speech uses the pinned model");
                var expectedVoice = calls == 1 ? "marin" : "cedar";
                var expectedSpeed = calls == 1 ? 1.0 : 1.15;
                Require(body.RootElement.GetProperty("voice").GetString() == expectedVoice
                    && body.RootElement.GetProperty("speed").GetDouble() == expectedSpeed
                    && body.RootElement.GetProperty("response_format").GetString() == "pcm"
                    && body.RootElement.GetProperty("input").GetString() == "Ahoj, kouzelníku!",
                    "speech uses the chosen voice and exactly the validated text");
                Require(body.RootElement.GetProperty("instructions").GetString()!.Contains("bez dlouhých umělých pauz"),
                    "server owns brisk Czech speech instructions");
                return Response(Tone(24_000, 800, 24_000), "application/octet-stream");
            }

            var upload = await request.Content!.ReadAsStringAsync(cancellation);
            Require(request.Content.Headers.ContentType!.MediaType == "multipart/form-data"
                && upload.Contains("name=model\r\n\r\ngpt-transcribe")
                && upload.Contains("name=\"languages[]\"\r\n\r\ncs"),
                "SDK multipart preserves GPT-Transcribe and Czech languages[]");
            Require(upload.Contains("Content-Type: audio/wav") && upload.Contains("RIFF"),
                "provider receives WAV without identity or history");
            return Response(Encoding.UTF8.GetBytes("{\"text\":\"Jmenuju se Marek.\",\"languages\":[]}"), "application/json");
        }));
        var output = await new OpenAiSpeechClient(http, "test-only").GenerateAsync("Ahoj, kouzelníku!", default);
        Require(output.Length == 64_044, "actual PCM response is converted to device WAV");
        output = await new OpenAiSpeechClient(http, "test-only").GenerateAsync(
            "Ahoj, kouzelníku!", new SpeechOptions("cedar", 1.15), default);
        Require(output.Length == 64_044, "selected voice and speed preserve the WAV contract");
        var text = await new OpenAiTranscriptionClient(http, "test-only")
            .TranscribeAsync(DeviceWav(), default);
        Require(text == "Jmenuju se Marek." && calls == 3, "transcript is parsed without rewriting it");
    }

    private static async Task CheckRejectedProviderResponsesAsync()
    {
        foreach (var response in new[]
        {
            Response(new byte[3], "audio/pcm"),
            Response(new byte[PcmSpeechAudio.MaxProviderBytes + 2], "audio/pcm"),
            Response(new byte[100], "audio/mpeg"),
            new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
        })
        {
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(response)));
            await RejectAsync(() => new OpenAiSpeechClient(http, "test-only").GenerateAsync("Ahoj", default));
        }

        foreach (var body in new[] { "{}", "{\"text\":\"\"}", "{\"text\":\"a\",\"text\":\"b\"}",
                     JsonSerializer.Serialize(new { text = new string('a', 1001) }), new string('x', 16_385) })
        {
            using var http = new HttpClient(new Handler((_, _) =>
                Task.FromResult(Response(Encoding.UTF8.GetBytes(body), "application/json"))));
            await RejectAsync(() => new OpenAiTranscriptionClient(http, "test-only").TranscribeAsync(DeviceWav(), default));
        }

        var unavailableCalls = 0;
        using (var unavailableHttp = new HttpClient(new Handler((_, _) =>
        {
            unavailableCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("provider-private-error")
            });
        })))
        {
            await RejectAsync(() => new OpenAiSpeechClient(unavailableHttp, "test-only").GenerateAsync("Ahoj", default));
            await RejectAsync(() => new OpenAiTranscriptionClient(unavailableHttp, "test-only").TranscribeAsync(DeviceWav(), default));
        }
        Require(unavailableCalls == 2, "SDK does not retry failed speech or transcription requests");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var blockedHttp = new HttpClient(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response([], "audio/pcm");
        }));
        await RejectAsync(() => new OpenAiSpeechClient(blockedHttp, "test-only")
            .GenerateAsync("Ahoj", cancelled.Token));
    }

    private static async Task CheckRuntimeAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"kufr-voice-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await CheckTranscriptionLifecycleAsync(directory);
            await CheckSpeechLifecycleAsync(directory);
            await CheckCancellationAsync(directory);
            await CheckDeadlineAndOversizeAsync(directory);
            await CheckCostAndConsentAsync(directory);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task CheckTranscriptionLifecycleAsync(string directory)
    {
        var started = Completion<byte[]>();
        var finish = Completion<string>();
        using var runner = new VoiceProviderRunner((bytes, _) =>
        {
            started.SetResult(bytes);
            return finish.Task;
        },
            (_, _) => Task.FromResult(DeviceWav()));
        using var fixture = new Fixture(directory, "transcription", runner);
        var upload = fixture.Upload();
        var key = Guid.NewGuid();
        var accepted = fixture.Store.SubmitTranscription("device-a", fixture.Id, key, upload);
        var ownedInput = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(!ReferenceEquals(upload.Wav, ownedInput), "queued input has exclusive lifetime");
        Require(fixture.Store.GetOperation("device-a", accepted.OperationId!).Operation?.Status == "running",
            "provider operation polls as running");
        Require(fixture.Store.SubmitTranscription("device-a", fixture.Id, key, upload).OperationId == accepted.OperationId,
            "replay cannot invoke another provider call");
        Require(fixture.Store.SubmitTranscription("device-a", fixture.Id, key,
            upload with { DeviceRevision = 9 }).Status == 409, "changed retry conflicts");
        Require(fixture.Store.SubmitTurn("device-a", fixture.Id, Guid.NewGuid(), "turn"u8, fixture.Turn()).Status == 409,
            "a text turn cannot race transcription");
        CryptographicOperations.ZeroMemory(upload.Wav);
        Require(ownedInput.Any(value => value != 0), "endpoint cleanup cannot erase queued input");
        finish.SetResult("Čau, já jsem Marek.");
        var operation = await PollAsync(fixture.Store, accepted.OperationId!, "completed");
        Require(operation.Result is TranscriptionResult { Transcript: "Čau, já jsem Marek." }, "real result is available");
        Require(ownedInput.All(value => value == 0), "completed provider input is wiped");
        fixture.RequireReservation(accepted.OperationId!, VoiceProviderRunner.TranscriptionReservationMicroUsd);
    }

    private static async Task CheckSpeechLifecycleAsync(string directory)
    {
        var output = DeviceWav();
        string? spoken = null;
        SpeechOptions? chosenOptions = null;
        using var runner = new VoiceProviderRunner((_, _) => Task.FromResult("ahoj"),
            (reply, options, _) =>
            {
                spoken = reply;
                chosenOptions = options;
                return Task.FromResult(output);
            });
        using var fixture = new Fixture(directory, "speech", runner);
        var turn = fixture.Turn();
        fixture.Store.SubmitTurn("device-a", fixture.Id, Guid.NewGuid(), "turn"u8, turn);
        var key = Guid.NewGuid();
        var selectedSpeech = new SpeechRequest(turn.ClientTurnId, "device", "cedar", 1.15);
        var accepted = fixture.Store.SubmitSpeech("device-a", fixture.Id, key, "speech"u8,
            selectedSpeech);
        var operation = await PollAsync(fixture.Store, accepted.OperationId!, "completed");
        var result = (SpeechResult)operation.Result!;
        Require(fixture.Store.SubmitSpeech("device-a", fixture.Id, key, "speech"u8,
            selectedSpeech).OperationId == accepted.OperationId,
            "speech replay returns the same audio without another charge");
        Require(fixture.Store.SubmitSpeech("device-a", fixture.Id, key, "changed-speech"u8,
            selectedSpeech with { Speed = 1.2 }).ErrorCode == "idempotency_conflict",
            "changed speech options cannot reuse the idempotency key");
        Require(spoken == "Toto je testovací odpověď bez AI.", "TTS receives only the stored validated reply");
        Require(chosenOptions is { Voice: "cedar", Speed: 1.15 },
            "selected speech options reach the provider");
        Require(fixture.Store.GetAudio("other", result.AudioId).Status == 403, "other device cannot read speech");
        Require(fixture.Store.GetAudio("device-a", result.AudioId).Wav!.SequenceEqual(output), "WAV is downloadable");
        Require(fixture.Store.SubmitSpeech("device-a", fixture.Id, Guid.NewGuid(), "speech-2"u8,
            new SpeechRequest(turn.ClientTurnId, "phone")).ErrorCode == "speech_output_active", "one output target at a time");
        fixture.RequireReservation(accepted.OperationId!, VoiceProviderRunner.SpeechReservationMicroUsd);

        var endingStarted = Completion<bool>();
        var endingFinish = Completion<string>();
        using var endingRunner = new VoiceProviderRunner((_, _) =>
        {
            endingStarted.SetResult(true);
            return endingFinish.Task;
        }, (_, _) => Task.FromResult(DeviceWav()));
        using var ending = new Fixture(directory, "ending-stt", endingRunner);
        var endingOperation = ending.Store.SubmitTranscription("device-a", ending.Id, Guid.NewGuid(), ending.Upload());
        await endingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(ending.Store.EndConversation("device-a", ending.Id, Guid.NewGuid()).Status == 204,
            "ending conversation cancels in-flight transcription");
        endingFinish.SetResult("late transcript");
        await PollAsync(ending.Store, endingOperation.OperationId!, "cancelled");
        ending.RequireReservation(endingOperation.OperationId!, VoiceProviderRunner.TranscriptionReservationMicroUsd);
        fixture.Clock.Now += TimeSpan.FromSeconds(121);
        Require(fixture.Store.GetAudio("device-a", result.AudioId).Status == 410, "audio expires after 120 seconds");
        Require(output.All(value => value == 0), "expired WAV samples are wiped");
    }

    private static async Task CheckCancellationAsync(string directory)
    {
        var started = Completion<bool>();
        var finish = Completion<byte[]>();
        using var runner = new VoiceProviderRunner((_, _) => Task.FromResult("ahoj"),
            (_, _) =>
            {
                started.SetResult(true);
                return finish.Task;
            });
        using var fixture = new Fixture(directory, "cancel", runner);
        var turn = fixture.Turn();
        fixture.Store.SubmitTurn("device-a", fixture.Id, Guid.NewGuid(), "turn"u8, turn);
        var accepted = fixture.Store.SubmitSpeech("device-a", fixture.Id, Guid.NewGuid(), "speech"u8,
            new SpeechRequest(turn.ClientTurnId, "device"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(fixture.Store.CancelOperation("device-a", accepted.OperationId!, Guid.NewGuid()).Status == 204,
            "in-flight speech can be cancelled");
        var lateOutput = DeviceWav();
        finish.SetResult(lateOutput); // A provider may ignore cancellation and return a late response.
        await PollAsync(fixture.Store, accepted.OperationId!, "cancelled");
        Require(fixture.Store.CachedAudioCount == 0 && lateOutput.All(value => value == 0),
            "late speech cannot be published and is wiped");
        fixture.RequireReservation(accepted.OperationId!, VoiceProviderRunner.SpeechReservationMicroUsd);

        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();
        var calls = 0;
        using var stoppedRunner = new VoiceProviderRunner((_, _) =>
        {
            calls++;
            return Task.FromResult("ahoj");
        },
            (_, _) => Task.FromResult(DeviceWav()), shutdown.Token);
        using var stopped = new Fixture(directory, "stopped", stoppedRunner);
        var stoppedOperation = stopped.Store.SubmitTranscription("device-a", stopped.Id, Guid.NewGuid(), stopped.Upload());
        await PollAsync(stopped.Store, stoppedOperation.OperationId!, "failed");
        Require(calls == 0, "shutdown before dispatch makes no provider request");
        stopped.RequireReservation(stoppedOperation.OperationId!, 0, settled: true);
    }

    private static async Task CheckDeadlineAndOversizeAsync(string directory)
    {
        var lateOutput = DeviceWav();
        using var deadlineRunner = new VoiceProviderRunner(
            async (_, _) =>
            {
                await Task.Delay(50);
                return "late transcript";
            },
            async (_, _) =>
            {
                await Task.Delay(50);
                return lateOutput;
            },
            timeout: TimeSpan.FromMilliseconds(10));
        using var transcription = new Fixture(directory, "deadline-stt", deadlineRunner);
        var accepted = transcription.Store.SubmitTranscription(
            "device-a", transcription.Id, Guid.NewGuid(), transcription.Upload());
        await PollAsync(transcription.Store, accepted.OperationId!, "failed");
        transcription.RequireReservation(accepted.OperationId!, VoiceProviderRunner.TranscriptionReservationMicroUsd);

        using var speech = new Fixture(directory, "deadline-tts", deadlineRunner);
        var turn = speech.Turn();
        speech.Store.SubmitTurn("device-a", speech.Id, Guid.NewGuid(), "turn"u8, turn);
        accepted = speech.Store.SubmitSpeech("device-a", speech.Id, Guid.NewGuid(), "speech"u8,
            new SpeechRequest(turn.ClientTurnId, "device"));
        await PollAsync(speech.Store, accepted.OperationId!, "failed");
        Require(lateOutput.All(value => value == 0) && speech.Store.CachedAudioCount == 0,
            "a provider ignoring its deadline cannot publish audio");
        speech.RequireReservation(accepted.OperationId!, VoiceProviderRunner.SpeechReservationMicroUsd);

        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(
            Response(new byte[PcmSpeechAudio.MaxProviderBytes + 2], "audio/pcm"))));
        using var oversizedRunner = new VoiceProviderRunner(
            (_, _) => Task.FromResult("ahoj"),
            (reply, cancellationToken) =>
                new OpenAiSpeechClient(http, "test-only").GenerateAsync(reply, cancellationToken));
        using var oversized = new Fixture(directory, "oversized-tts", oversizedRunner);
        turn = oversized.Turn();
        oversized.Store.SubmitTurn("device-a", oversized.Id, Guid.NewGuid(), "turn"u8, turn);
        accepted = oversized.Store.SubmitSpeech("device-a", oversized.Id, Guid.NewGuid(), "speech"u8,
            new SpeechRequest(turn.ClientTurnId, "device"));
        await PollAsync(oversized.Store, accepted.OperationId!, "failed");
        Require(oversized.Store.CachedAudioCount == 0, "speech over 30 seconds has no partial playback");
        oversized.RequireReservation(accepted.OperationId!, VoiceProviderRunner.SpeechReservationMicroUsd);
    }

    private static async Task CheckCostAndConsentAsync(string directory)
    {
        var calls = 0;
        using var runner = new VoiceProviderRunner((_, _) =>
        {
            calls++;
            throw new HttpRequestException("synthetic");
        },
            (_, _) => Task.FromResult(DeviceWav()));
        using var rejected = new Fixture(directory, "cost", runner, cap: 9_999);
        var lowCapCall = rejected.Store.SubmitTranscription("device-a", rejected.Id, Guid.NewGuid(), rejected.Upload());
        Require(lowCapCall.Status == 202, "legacy low monetary cap cannot block voice");
        await PollAsync(rejected.Store, lowCapCall.OperationId!, "failed");
        using var noConsent = new Fixture(directory, "consent", runner, consent: false);
        Require(noConsent.Store.SubmitTranscription("device-a", noConsent.Id, Guid.NewGuid(), noConsent.Upload()).Status
            == 403 && calls == 1, "audio consent is checked before dispatch");
        using var failed = new Fixture(directory, "failure", runner);
        var failure = failed.Store.SubmitTranscription("device-a", failed.Id, Guid.NewGuid(), failed.Upload());
        await PollAsync(failed.Store, failure.OperationId!, "failed");
        Require(calls == 2, "failed provider is not retried automatically");
        failed.RequireReservation(failure.OperationId!, VoiceProviderRunner.TranscriptionReservationMicroUsd);
    }

    private static async Task<TurnOperation> PollAsync(ConversationStore store, string id, string status)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var operation = store.GetOperation("device-a", id).Operation;
            if (operation?.Status == status && store.ActiveProviderCallCount == 0)
            {
                return operation;
            }

            await Task.Delay(10);
        }

        throw new Exception($"Voice operation did not reach {status}.");
    }

    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static byte[] DeviceWav() => PcmSpeechAudio.ToDeviceWav(Tone(24_000, 1_000, 12_000));
    private static byte[] Tone(int rate, int frequency, int samples)
    {
        var pcm = new byte[samples * 2];
        for (var index = 0; index < samples; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(index * 2),
                (short)Math.Round(10_000 * Math.Sin(2 * Math.PI * frequency * index / rate)));
        }

        return pcm;
    }

    private static HttpResponseMessage Response(byte[] bytes, string type)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new(type);
        return response;
    }

    private static async Task RejectAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception error) when (error is InvalidDataException
            or HttpRequestException
            or OperationCanceledException
            or JsonException)
        {
            return;
        }
        throw new Exception("Invalid provider result was accepted.");
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new Exception("Invalid request or configuration was accepted.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly OperationJournal journal;
        public Fixture(string directory, string name, VoiceProviderRunner runner, long cap = 200_000, bool consent = true)
        {
            Path = System.IO.Path.Combine(directory, name + ".sqlite");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:DatabasePath"] = Path,
                ["Backend:Usage:SessionCapMicroUsd"] = cap.ToString(),
                ["Backend:Usage:DailyCapMicroUsd"] = "1000000",
                ["Backend:Usage:MonthlyCapMicroUsd"] = "10000000"
            }).Build();
            journal = new OperationJournal(configuration, Clock);
            Store = new ConversationStore(Clock, journal, voiceRunner: runner);
            Id = Store.Create("device-a", Guid.NewGuid(), "create"u8,
                new CreateConversationRequest("device-a", Boot, 0, "catalog-1", "cs-CZ",
                    new ConversationConsent(consent, false))).Response!.ConversationId;
        }

        public string Path { get; }
        public Clock Clock { get; } = new();
        public ConversationStore Store { get; }
        public Guid Boot { get; } = Guid.NewGuid();
        public string Id { get; }
        public TextTurnRequest Turn() => new(Guid.NewGuid(), Boot, 0, "synthetic", 63, 7, "catalog-1");
        public TranscriptionUpload Upload() => new(Guid.NewGuid(), Boot, 0, 7, DeviceWav());
        public void Dispose() => journal.Dispose();
        public void RequireReservation(string id, long amount, bool settled = false)
        {
            using var connection = new SqliteConnection($"Data Source={Path}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT reserved_micro_usd, actual_micro_usd, status FROM cost_reservations WHERE operation_id=$id";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            Require(reader.Read(), "reservation is durable");
            Require(settled ? reader.GetInt64(1) == amount && reader.GetString(2) == "settled"
                : reader.GetInt64(0) == amount && reader.IsDBNull(1) && reader.GetString(2) == "reserved",
                "audio billing is conservative without fabricated actual usage");
        }
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() { }
    }
}
