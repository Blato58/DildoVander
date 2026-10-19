using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Show;
using Kufr.Backend.Features.Speech;
using Kufr.Backend.Features.VoiceTurns;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Access;

internal static class ShowDeviceCueTests
{
    internal static async Task RunAsync()
    {
        await NarrationUsesOneBackgroundClaimAsync();
        await CancellationAndFailedClaimsNeverReplayAsync();
        await QueueAndExpiryBoundsAsync();
        await AmbientUsesActualPlaybackEvidenceAsync();
        await TypedSceneCuesAsync();
        await HttpContractsAsync();
        await ExplicitFakeModeAsync();
        await ActualDuelBridgeCompositionAsync();
        Console.WriteLine("Device cue mailbox tests passed with fake provider only; hardware playback remains separate evidence.");
    }

    private static async Task NarrationUsesOneBackgroundClaimAsync()
    {
        using var fixture = new Fixture();
        var bridge = fixture.Bridge;
        Check(bridge.DeviceNarrationUnavailableReason(Device) == "device_not_connected", "absent device is explicit web-only");
        bridge.Poll(Device, fixture.Request() with { AppliedSettingsRevision = null, AppliedQuiet = null, AppliedAmbient = null });
        Check(bridge.DeviceNarrationUnavailableReason(Device) == "device_settings_unconfirmed", "presence alone cannot claim compatible mode");
        Check(fixture.Calls == 0 && fixture.Sql("SELECT count(*) FROM turn_operations") == 0, "poll cannot prepare or bill audio");
        fixture.Poll();
        Check(bridge.DeviceNarrationUnavailableReason(Device) is null, "recent settings-confirmed device supports narration");
        var duel = fixture.Occupy();
        Check(bridge.EnqueueDuelLine(Device, duel, 0, "wake", 2, "Souboj začíná."), "accepted intent enqueues intro");
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cue = fixture.Poll().Cue!;
        Check(cue is { SceneId: "wake", Compartment: 2, Voice: "cedar", CueType: "NarratedBeat" }
            && cue.BootId == fixture.Boot && cue.Generation == 4 && cue.DeviceRevision == 7
            && cue.IssuedAtDeviceMs == 1234 && cue.ValidUntilDeviceMs == 3234
            && cue.AudioPath == $"/v1/show/device-cues/{cue.CueId:D}/audio", "immutable echoed admission snapshot and chosen voice");
        Check(fixture.Poll().Cue!.CueId == cue.CueId && fixture.Calls == 1, "poll retry has same cue and no new provider claim");
        Check(bridge.ReadAudio(Device, cue.CueId).Status == 409, "audio requires actual admission ack");
        Check(bridge.Acknowledge(Device, cue.CueId, fixture.Ack("accepted", "playback_finished")).Status == 400,
            "accepted cannot pretend playback finished");
        var accepted = fixture.Ack("accepted", "commanded");
        Check(bridge.Acknowledge(Device, cue.CueId, accepted).Status == 200, "accepted records queued proxy");
        Check(bridge.Acknowledge(Device, cue.CueId, accepted).Code == "ack_replayed"
            && bridge.Acknowledge(Device, cue.CueId, accepted with { Reason = "changed" }).Status == 409,
            "ack identity binds payload");
        Check(bridge.ReadAudio(Device, cue.CueId).Status == 202 && bridge.ReadAudio(Device, cue.CueId).Status == 202
            && fixture.Calls == 1 && bridge.DuelNarrationState(Device, duel) == "pending", "GET stays immediate pending without rebill");
        Check(bridge.Acknowledge(Device, cue.CueId, fixture.Ack("completed", "commanded")).Status == 409,
            "commanded proxy cannot complete speech");
        Check(bridge.Acknowledge("other-device", cue.CueId, fixture.Ack("accepted", "commanded")).Status == 409
            && bridge.Acknowledge(Device, cue.CueId, accepted with { DeviceRevision = 6 }).Status == 409,
            "device and snapshot scope are mandatory");
        fixture.Audio.TrySetResult(Wav());
        await bridge.AudioWork(Device, cue.CueId).WaitAsync(TimeSpan.FromSeconds(5));
        var audio = bridge.ReadAudio(Device, cue.CueId);
        Check(audio.Status == 200 && audio.Wav!.Length <= ShowDeviceCueBridge.MaximumWavBytes
            && fixture.LastVoice == "cedar" && fixture.Sql("SELECT count(*) FROM turn_operations WHERE kind='speech'") == 1
            && fixture.Sql("SELECT sum(reserved_micro_usd) FROM cost_reservations") == VoiceProviderRunner.SpeechReservationMicroUsd,
            "one audited provider job produces bounded chosen-voice PCM32k WAV");
        Check(bridge.Acknowledge(Device, cue.CueId, fixture.Ack("completed", "playback_finished")).Status == 200
            && bridge.DuelNarrationState(Device, duel) == "idle", "actual player completion drains narration");
        Check(bridge.Acknowledge(Device, cue.CueId, fixture.Ack("completed", "playback_finished")).Code == "ack_replayed",
            "completion retry cannot consume another segment");
        Check(bridge.EnqueueDuelLine(Device, duel, 0, "wake", 2, "Souboj začíná.") && fixture.Poll().Cue is null
            && !bridge.EnqueueDuelLine(Device, duel, 0, "wake", 3, "Změněný obsah."), "enqueue retries bind identity and payload");
        await fixture.FinishAsync();
    }

    private static async Task CancellationAndFailedClaimsNeverReplayAsync()
    {
        using (var fixture = new Fixture())
        {
            fixture.Poll();
            var duel = fixture.Occupy();
            fixture.Bridge.EnqueueDuelLine(Device, duel, 1, "consider", 1, "První kolo.");
            await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var cue = fixture.Poll().Cue!;
            fixture.Bridge.Acknowledge(Device, cue.CueId, fixture.Ack("accepted", "commanded"));
            fixture.Command("stop");
            var cancelled = fixture.Poll();
            Check(cancelled.Cue is null && cancelled.CancelledCueId == cue.CueId && cancelled.StopRevision > 0,
                "actual backend Stop revokes accepted cue and reports cancellation");
            Check(fixture.Bridge.ReadAudio(Device, cue.CueId).Status == 409
                && fixture.Bridge.Acknowledge(Device, cue.CueId, fixture.Ack("completed", "playback_finished")).Status == 409,
                "Stop rejects stale audio and completion");
            await fixture.Bridge.AudioWork(Device, cue.CueId).WaitAsync(TimeSpan.FromSeconds(5));
            Check(fixture.Bridge.DuelNarrationState(Device, duel) == "failed" && fixture.Calls == 1,
                "cancelled provider job cannot revive narration");
            await fixture.FinishAsync();
        }
        using (var fixture = new Fixture())
        {
            fixture.Poll();
            var duel = fixture.Occupy();
            fixture.Bridge.EnqueueDuelLine(Device, duel, 2, "consider", 1, "Druhé kolo.");
            var cue = fixture.Poll().Cue!;
            fixture.Bridge.Acknowledge(Device, cue.CueId, fixture.Ack("accepted", "commanded"));
            fixture.Audio.TrySetException(new IOException("synthetic provider failure"));
            await fixture.Bridge.AudioWork(Device, cue.CueId).WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Bridge.Tick();
            Check(fixture.Bridge.ReadAudio(Device, cue.CueId).Status is 409 or 503
                && fixture.Bridge.DuelNarrationState(Device, duel) == "failed"
                && !fixture.Bridge.EnqueueDuelLine(Device, duel, 2, "consider", 1, "Druhé kolo.") && fixture.Calls == 1,
                "failed audit claim never automatically calls provider again");
            await fixture.FinishAsync();
        }
    }

    private static async Task QueueAndExpiryBoundsAsync()
    {
        using var fixture = new Fixture();
        fixture.Poll();
        var duel = fixture.Occupy();
        Check(fixture.Bridge.EnqueueDuelLine(Device, duel, 0, "wake", 1, "Úvod.")
            && fixture.Bridge.EnqueueDuelLine(Device, duel, 1, "consider", 1, "Jedna.")
            && fixture.Bridge.EnqueueDuelLine(Device, duel, 2, "consider", 1, "Dva.")
            && fixture.Bridge.EnqueueDuelLine(Device, duel, 3, "consider", 1, "Tři.")
            && !fixture.Bridge.EnqueueDuelLine(Device, duel, 3, "farewell", 1, "Konec."), "queue capacity is four; final has separate round-three identity");
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var first = fixture.Poll().Cue!;
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(2001));
        Check(fixture.Poll().CancelledCueId == first.CueId
            && fixture.Bridge.Acknowledge(Device, first.CueId, fixture.Ack("accepted", "commanded")).Status == 409,
            "two-second admission cannot be extended by poll replay");
        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        fixture.Bridge.Tick();
        Check(fixture.Poll().Cue is null && fixture.Bridge.DuelNarrationState(Device, duel) == "failed", "unpolled queue also expires");
        fixture.Show.Release(Device, duel);
        var next = fixture.Occupy();
        fixture.Bridge.EnqueueDuelLine(Device, next, 3, "consider", 1, "Třetí kolo.");
        var cue = fixture.Poll().Cue!;
        fixture.Bridge.Acknowledge(Device, cue.CueId, fixture.Ack("accepted", "commanded"));
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        Check(fixture.Poll().Cue?.CueId == cue.CueId, "accepted playback lease exceeds admission TTL");
        fixture.Clock.Advance(TimeSpan.FromSeconds(18));
        Check(fixture.Poll().Cue is null && fixture.Bridge.DuelNarrationState(Device, next) == "failed", "accepted lease is finite twenty seconds");
        fixture.Clock.Advance(TimeSpan.FromSeconds(11));
        Check(!fixture.Bridge.HasRecentDevicePoll(Device), "capability presence expires in ten seconds");
        await fixture.FinishAsync();
    }

    private static async Task AmbientUsesActualPlaybackEvidenceAsync()
    {
        using var fixture = new Fixture();
        fixture.Command("ambient", "on");
        fixture.Clock.Advance(TimeSpan.FromSeconds(181));
        fixture.Poll();
        Check(fixture.Calls == 0 && fixture.Poll().Cue is null, "idle polls alone do not generate ambient speech");
        fixture.Bridge.Tick();
        var cue = fixture.Poll().Cue!;
        Check(cue.SceneId == "ambient" && cue.DuelId is null && cue.SpeechText.Length > 0, "background tick consumes authored actual ambient line");
        var source = fixture.Show.Read(Device).PendingCue!;
        fixture.Bridge.Acknowledge(Device, cue.CueId, fixture.Ack("accepted", "commanded"));
        Check(!fixture.Show.Read(Device).RecentAmbientIds!.Contains(source.Value), "queued proxy does not mark line spoken");
        fixture.Audio.TrySetResult(Wav());
        await fixture.Bridge.AudioWork(Device, cue.CueId).WaitAsync(TimeSpan.FromSeconds(5));
        Check(fixture.Bridge.Acknowledge(Device, cue.CueId, fixture.Ack("completed", "playback_finished")).Status == 200
            && fixture.Show.Read(Device).RecentAmbientIds!.Contains(source.Value), "only actual playback ack records ambient use");
        fixture.Command("quiet", "on");
        fixture.Clock.Advance(TimeSpan.FromMinutes(4));
        fixture.Poll();
        fixture.Bridge.Tick();
        Check(fixture.Poll().Cue is null && fixture.Calls == 1, "quiet prevents scheduled ambient provider calls");
        await fixture.FinishAsync();
    }

    private static async Task TypedSceneCuesAsync()
    {
        using var fixture = new Fixture();
        fixture.Poll();
        using var visit = fixture.Show.BeginVisit(Device, fixture.EveningId, "scene-session", "scene-generation");
        ShowCue Cue(string id, string kind, string value) => new(id, kind, "scene_started", 0, 8000, "drop", "scene-generation", value);
        var led = Cue("led", "LedPreset", "consider");
        Check(fixture.Bridge.EnqueueSceneCue(Device, "wrong-generation", led) == "stale"
            && fixture.Bridge.EnqueueSceneCue(Device, "scene-generation", Cue("unsafe", "VibratePreset", "arbitrary-pulse")) == "unsupported_capability",
            "scene adapter validates generation and finite allowlists");
        Check(fixture.Bridge.EnqueueSceneCue(Device, "scene-generation", led) == "queued"
            && fixture.Bridge.ReadSceneCueStatus(Device, "scene-generation", "led") == "queued", "typed scene enters existing mailbox");
        var cue = fixture.Poll().Cue!;
        Check(cue is { CueType: "LedPreset", Preset: "consider", AudioPath: null, SpeechText: "" }, "LED uses one authored preset without speech or motor command");
        fixture.Bridge.Acknowledge(Device, cue.CueId, fixture.Ack("accepted", "commanded"));
        fixture.Bridge.Acknowledge(Device, cue.CueId, fixture.Ack("completed", "commanded"));
        Check(fixture.Bridge.ReadSceneCueStatus(Device, "scene-generation", "led") == "completed", "timed local preset completion remains command evidence");
        fixture.Bridge.EnqueueSceneCue(Device, "scene-generation", Cue("clip", "PlayLocalClip", "intro-1"));
        cue = fixture.Poll().Cue!;
        Check(cue is { CueType: "PlayLocalClip", ClipId: "intro-1", AudioPath: null }, "local bank clip needs no new provider");
        fixture.Bridge.Acknowledge(Device, cue.CueId, fixture.Ack("accepted", "commanded"));
        Check(fixture.Bridge.Acknowledge(Device, cue.CueId, fixture.Ack("completed", "commanded")).Status == 409
            && fixture.Bridge.Acknowledge(Device, cue.CueId, fixture.Ack("completed", "playback_finished")).Status == 200,
            "bank clip also requires player evidence");
        fixture.Bridge.EnqueueSceneCue(Device, "scene-generation", Cue("vibration", "VibratePreset", "presentation-search-pulse") with { TargetSlot = 2 });
        cue = fixture.Poll().Cue!;
        fixture.Bridge.Acknowledge(Device, cue.CueId, fixture.Ack("rejected", "missing_assets"));
        Check(fixture.Bridge.ReadSceneCueStatus(Device, "scene-generation", "vibration") == "failed" && fixture.Calls == 0,
            "actual missing adapter rejects explicitly and presets never call provider");
        fixture.Bridge.EnqueueSceneCue(Device, "scene-generation", Cue("voice-fence", "LedPreset", "wake"));
        cue = fixture.Poll().Cue!;
        var changed = fixture.Bridge.Poll(Device, fixture.Request() with { Voice = "coral" });
        Check(changed.CancelledCueId == cue.CueId && changed.Cue is null
            && fixture.Bridge.ReadSceneCueStatus(Device, "scene-generation", "voice-fence") == "cancelled", "local voice change fences existing captured audio/cues");
        await fixture.FinishAsync();
    }

    private static async Task HttpContractsAsync()
    {
        using var fixture = new Fixture();
        fixture.Poll();
        var duel = fixture.Occupy();
        fixture.Bridge.EnqueueDuelLine(Device, duel, 0, "wake", 2, "Skutečný HTTP kontrakt.");
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cue = fixture.Poll().Cue!;
        var access = new DeviceAccessStore(fixture.Configuration, fixture.Clock);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(access);
        builder.Services.AddSingleton(fixture.Bridge);
        await using var app = builder.Build();
        app.MapShowDeviceCues();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        var poll = $"/v1/show/device-cues?bootId={fixture.Boot:D}&generation=4&deviceRevision=7&deviceTimeMs=1234&appliedSettingsRevision=0&appliedQuiet=false&appliedAmbient=false&voice=cedar";
        Check((await client.GetAsync(poll)).StatusCode == HttpStatusCode.Unauthorized, "real poll rejects missing bearer");
        client.DefaultRequestHeaders.Authorization = new("Bearer", access.IssueToken("other-device")!.AccessToken);
        Check((await client.GetAsync(cue.AudioPath)).StatusCode == HttpStatusCode.Conflict,
            "other device token cannot fetch scoped cue audio");
        var ackPath = $"/v1/show/device-cues/{cue.CueId:D}/ack";
        Check((await client.PostAsJsonAsync(ackPath, fixture.Ack("accepted", "commanded"))).StatusCode == HttpStatusCode.Conflict,
            "other device token cannot ack scoped cue");
        client.DefaultRequestHeaders.Authorization = new("Bearer", access.IssueToken(Device)!.AccessToken);
        var response = await client.GetAsync(poll);
        var envelope = await response.Content.ReadFromJsonAsync<ShowDeviceCueEnvelope>();
        Check(response.StatusCode == HttpStatusCode.OK && envelope!.Cue!.CueId == cue.CueId
            && response.Headers.CacheControl?.NoStore == true && fixture.Calls == 1,
            "authenticated route returns actual mailbox without provider work");
        foreach (var invalidQuery in new[] { "&generation=4", "&deviceId=other-device", "&voice=unsafe" })
        { Check((await client.GetAsync(poll + invalidQuery)).StatusCode == HttpStatusCode.BadRequest, "strict poll query " + invalidQuery); }
        var json = JsonSerializer.Serialize(fixture.Ack("accepted", "commanded"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var invalidJson in new[]
        {
            json[..^1] + ",\"generation\":4}",
            json[..^1] + ",\"unknown\":true}",
            "{\"bootId\":\"" + fixture.Boot.ToString("D") + "\",\"outcome\":\"accepted\",\"evidence\":\"commanded\"}"
        })
        { Check((await client.PostAsync(ackPath, new StringContent(invalidJson, Encoding.UTF8, "application/json"))).StatusCode == HttpStatusCode.BadRequest, "strict ack duplicate/unknown/missing fields"); }
        Check((await client.PostAsync(ackPath, new StringContent(new string('x', 2049), Encoding.UTF8, "application/json"))).StatusCode == HttpStatusCode.RequestEntityTooLarge,
            "ack body is bounded before parsing");
        Check((await client.PostAsJsonAsync(ackPath, fixture.Ack("accepted", "commanded"))).StatusCode == HttpStatusCode.OK
            && (await client.GetAsync(cue.AudioPath)).StatusCode == HttpStatusCode.Accepted && fixture.Calls == 1,
            "real audio GET returns immediate pending without retry billing");
        fixture.Audio.TrySetResult(Wav());
        await fixture.Bridge.AudioWork(Device, cue.CueId).WaitAsync(TimeSpan.FromSeconds(5));
        response = await client.GetAsync(cue.AudioPath);
        Check(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == "audio/wav"
            && (await response.Content.ReadAsByteArrayAsync()).Length <= ShowDeviceCueBridge.MaximumWavBytes,
            "real route serves bounded PCM32k WAV");
        Check((await client.PostAsJsonAsync(ackPath, fixture.Ack("completed", "playback_finished"))).StatusCode == HttpStatusCode.OK,
            "real route records actual player finish");
        await app.StopAsync();
        await fixture.FinishAsync();
    }

    private static async Task ExplicitFakeModeAsync()
    {
        using var fixture = new Fixture("fake");
        fixture.Poll();
        Check(fixture.Bridge.DeviceNarrationUnavailableReason(Device) == "synthetic_speech_only",
            "explicit fake mode cannot claim physical narrated duel capability");
        var duel = fixture.Occupy();
        fixture.Bridge.EnqueueDuelLine(Device, duel, 0, "wake", 1, "Deterministický fake.");
        var cue = fixture.Poll().Cue!;
        await fixture.Bridge.AudioWork(Device, cue.CueId).WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Bridge.Acknowledge(Device, cue.CueId, fixture.Ack("accepted", "commanded"));
        Check(fixture.Bridge.ReadAudio(Device, cue.CueId).Status == 200 && fixture.Calls == 0
            && fixture.Sql("SELECT count(*) FROM cost_reservations") == 0, "explicit fake produces synthetic WAV without provider or fabricated usage");
        await fixture.FinishAsync();
    }

    private static async Task ActualDuelBridgeCompositionAsync()
    {
        using var fixture = new Fixture();
        fixture.Audio.TrySetResult(Wav());
        fixture.Poll();
        var identities = new DuelIdentities(fixture.EveningId);
        var store = new DuelStore(fixture.Configuration, fixture.Clock, identities,
            new DuelShowSlot(fixture.Show, fixture.Bridge),
            DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(fixture.DirectoryPath, "duel-keys"))));
        var duel = store.Create(Device, new(fixture.EveningId, "left", "right", "autoplay", true, "device-bridge-create"));
        duel = store.Start(Device, duel.Id, new(duel.Revision, "device-bridge-start"));
        Check(duel.NarrationMode == "physical_device" && duel.Deadline is null,
            "actual duel adapter binds current device with configured fake provider transport");
        store.Tick();
        var expected = new[] { ("wake", 0), ("consider", 1), ("consider", 2), ("consider", 3), ("farewell", 3) };
        var seen = new HashSet<Guid>();
        for (var index = 0; index < expected.Length; index++)
        {
            fixture.Bridge.Tick();
            var cue = fixture.Poll().Cue ?? throw new InvalidOperationException(
                $"Expected narrated duel cue {index}/{expected[index]}; duel={store.Read(duel.Id).State}/{store.Read(duel.Id).NarrationStatus}, "
                + $"mailbox={fixture.Bridge.DuelNarrationState(Device, duel.Id)}, calls={fixture.Calls}, "
                + $"occupation={fixture.Show.IsOccupationCurrent(Device, duel.Id)}, speechClaims={fixture.Sql("SELECT count(*) FROM turn_operations WHERE kind='speech'")}.");
            Check((cue.SceneId, cue.Round) == expected[index] && seen.Add(cue.CueId)
                && cue.SpeechText.Length > 0 && cue.DuelId?.ToString("D") == duel.Id,
                "actual authored intro/round/final mailbox cue " + index);
            Check(fixture.Bridge.Acknowledge(Device, cue.CueId, fixture.Ack("accepted", "commanded")).Status == 200,
                "actual duel device admission");
            await fixture.Bridge.AudioWork(Device, cue.CueId).WaitAsync(TimeSpan.FromSeconds(5));
            Check(fixture.Bridge.ReadAudio(Device, cue.CueId).Status == 200
                && store.Read(duel.Id) is { State: "Running", Result: null }, "queued/rendered speech cannot publish winner");
            store.Tick();
            Check(store.Read(duel.Id).Result is null && fixture.Poll().Cue?.CueId == cue.CueId,
                "actual pending playback applies backpressure");
            Check(fixture.Bridge.Acknowledge(Device, cue.CueId, fixture.Ack("completed", "playback_finished")).Status == 200,
                "exact actual player drain advances duel");
            store.Tick();
            if (index < 3)
            {
                fixture.Clock.Advance(TimeSpan.FromSeconds(3));
                fixture.Poll();
                store.Tick();
            }
        }
        Check(store.Read(duel.Id) is { State: "Completed", Result: not null, NarrationStatus: "playback_confirmed" }
            && fixture.Show.Read(Device).OccupiedBy is null && fixture.Calls == 5
            && fixture.Sql("SELECT count(*) FROM turn_operations WHERE kind='speech'") == 5,
            "real composed five-cue ACK flow completes once and releases authoritative show slot");
        await fixture.FinishAsync();
    }

    private sealed class DuelIdentities(string evening) : IDuelIdentitySource
    {
        public bool IsActiveEvening(string deviceId, string eveningId) => deviceId == Device && eveningId == evening;
        public bool IsOpenEvening(string deviceId, string eveningId) => IsActiveEvening(deviceId, eveningId);
        public DuelIdentitySnapshot? Read(string id, string eveningId, string deviceId) =>
            IsActiveEvening(deviceId, eveningId) && id is "left" or "right"
                ? DuelRules.Snapshot(id, "Archivář " + id,
                    PartyWandCatalog.Available(63).ElementAt(id == "left" ? 0 : 1).StableId) : null;
    }

    private const string Device = "show-device";
    private static byte[] Wav() => PcmSpeechAudio.ToDeviceWav(new byte[4800]);
    private static void Check(bool value, string name)
    { if (!value) { throw new InvalidOperationException("Device cue: " + name); } }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => now.UtcTicks;
        internal void Advance(TimeSpan span) => now += span;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "kufr-device-cue-" + Guid.NewGuid().ToString("N"));
        internal readonly Clock Clock = new();
        internal readonly OperationJournal Journal;
        internal readonly ShowCoordinator Show;
        internal readonly ShowDeviceCueBridge Bridge;
        internal readonly Guid Boot = Guid.NewGuid();
        internal readonly TaskCompletionSource<byte[]> Audio = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly string EveningId;
        internal readonly IConfiguration Configuration;
        internal string DirectoryPath => directory;
        internal string? LastVoice;
        internal int Calls;
        internal Fixture(string mode = "configured")
        {
            Directory.CreateDirectory(directory);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:DatabasePath"] = Path.Combine(directory, "journal.sqlite"),
                ["Devices:0:DeviceId"] = Device, ["Devices:1:DeviceId"] = "other-device",
                ["Devices:0:CredentialSha256"] = new string('a', 64), ["Devices:1:CredentialSha256"] = new string('b', 64),
                ["Show:DeviceCueSpeechMode"] = mode
            }).Build();
            Configuration = configuration;
            Journal = new OperationJournal(configuration, Clock);
            var memory = new EveningMemoryStore(configuration, Journal, Clock);
            EveningId = memory.GetStatus(Device).EveningId;
            Show = new ShowCoordinator(configuration, Journal, memory, Clock);
            var provider = new VoiceProviderRunner((_, _) => Task.FromResult("unused"),
                async (_, options, token) =>
                {
                    Interlocked.Increment(ref Calls);
                    LastVoice = options.Voice;
                    Started.TrySetResult();
                    // Each provider response owns its buffer. The bridge zeros
                    // completed audio; reusing the TCS template would corrupt
                    // the next synthetic response after the first cue drains.
                    return (await Audio.Task.WaitAsync(token)).ToArray();
                });
            provider.SetEnabled(true);
            Bridge = new ShowDeviceCueBridge(configuration, Show, Journal, provider, Clock);
        }
        internal ShowDeviceCuePoll Request()
        {
            var state = Show.Read(Device);
            return new(Boot, 4, 7, 1234, state.DeviceSettingsRevision, state.Quiet, state.AmbientEnabled, "cedar");
        }
        internal ShowDeviceCueEnvelope Poll() => Bridge.Poll(Device, Request());
        internal ShowDeviceCueAck Ack(string outcome, string evidence) => new(Boot, 4, 7, outcome, evidence);
        internal string Occupy()
        {
            var duel = Guid.NewGuid().ToString("D");
            Check(Show.TryOccupy(Device, EveningId, duel), "test duel owns existing slot");
            return duel;
        }
        internal void Command(string kind, string? value = null)
        {
            var state = Show.Read(Device);
            var result = Show.Command(Device, new(Guid.NewGuid().ToString("D"), state.Revision, kind, Value: value));
            Check(result.Status == "accepted", "operator " + kind + " accepted");
        }
        internal long Sql(string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "journal.sqlite") }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }
        internal async Task FinishAsync()
        {
            Bridge.CancelAll();
            Audio.TrySetResult(Wav());
            await Bridge.DrainAudioWork().WaitAsync(TimeSpan.FromSeconds(5));
        }
        public void Dispose()
        {
            Bridge.CancelAll();
            Audio.TrySetResult(Wav());
            Bridge.DrainAudioWork().GetAwaiter().GetResult();
            Journal.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
