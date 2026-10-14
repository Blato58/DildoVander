using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.VoiceTurns;
using Kufr.Backend.Features.Realtime;
using Kufr.Backend.Features.Diagnostics;
using Kufr.Backend.Features.Memory;
using Kufr.Backend.Features.Firmware;
using Kufr.Backend.Features.DigitalTwin;
using Microsoft.AspNetCore.RateLimiting;

var provisioning = args.Contains("--memory-provision");
var builder = WebApplication.CreateBuilder(args.Where(a => a is not ("--memory-provision" or "--execute" or "--create")).ToArray());
var twinConfiguration = Environment.GetEnvironmentVariable("KUFR_TWIN_CONFIG");
if (twinConfiguration is not null)
{
    if (!Path.IsPathFullyQualified(twinConfiguration)) { throw new InvalidOperationException("KUFR_TWIN_CONFIG must be absolute."); }
    builder.Configuration.AddJsonFile(twinConfiguration, optional: false, reloadOnChange: false);
    TwinProviderCredentials.Load(builder.Configuration);
}
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4096);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TwinAccess>();
builder.Services.AddSingleton<TwinMediaSettingsStore>();
builder.Services.AddSingleton<TwinMediaSessions>();
builder.Services.AddSingleton<TwinFakeInputPolicy>();
builder.Services.AddSingleton<TwinStore>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TwinStore>());
builder.Services.AddSingleton<TwinHost>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TwinHost>());
builder.Services.AddSingleton<DeviceAccessStore>();
builder.Services.AddSingleton<FirmwareReleases>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddConcurrencyLimiter("firmware", policy =>
    {
        policy.PermitLimit = 4;
        policy.QueueLimit = 0;
    });
});
builder.Services.AddSingleton<ServerAdminAccess>();
builder.Services.AddSingleton<LogReadAccess>();
builder.Services.AddSingleton<ServerLogStore>();
builder.Services.AddSingleton<ILoggerProvider, ServerLogProvider>();
builder.Services.AddSingleton<OperationJournal>();
builder.Services.AddSingleton<TextDialogRunner>();
builder.Services.AddSingleton<VoiceProviderRunner>();
builder.Services.AddSingleton<OpenAiRealtimeClient>();
builder.Services.AddSingleton<GptLiveClient>();
builder.Services.AddSingleton<RealtimeLiveRegistry>();
builder.Services.AddSingleton<EveningMemoryStore>();
builder.Services.AddSingleton(GptLiveOptions.Read(builder.Configuration));
builder.Services.AddSingleton(sp => HybridMemoryOptions.Read(builder.Configuration, sp.GetRequiredService<OperationJournal>()));
builder.Services.AddSingleton<WandDecisionService>();
builder.Services.AddSingleton<OpenAiVectorMemoryProvider>();
builder.Services.AddSingleton<HybridMemoryService>();
builder.Services.AddHostedService<MemoryIndexWorker>();
builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddHostedService<RetentionWorker>();

var app = builder.Build();
if (provisioning)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    Environment.ExitCode = await MemoryProvisioning.RunAsync(builder.Configuration,
        app.Services.GetRequiredService<OperationJournal>(), args, timeout.Token);
    await app.DisposeAsync();
    return;
}
app.Services.GetRequiredService<OperationJournal>();
app.Services.GetRequiredService<TextDialogRunner>();
app.Services.GetRequiredService<VoiceProviderRunner>();
// Per-instance settings enforce the fake/real boundary even when the isolated
// provider runner is available. Loading a key alone never starts an API call.
if (twinConfiguration is not null && builder.Configuration.GetValue<bool>("DigitalTwin:ExternalServicesAuthorized"))
{
    app.Services.GetRequiredService<TextDialogRunner>().SetEnabled(true);
}
var logs = app.Services.GetRequiredService<ServerLogStore>();
app.Services.GetRequiredService<LogReadAccess>();
logs.Write("info", "server_started");
app.Lifetime.ApplicationStopping.Register(() => logs.Write("info", "server_stopping"));

app.Use((context, next) => ServerLogEvents.RequestAsync(context, next, logs));
app.UseRateLimiter();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/v1"))
    {
        context.Response.Headers.CacheControl = "no-store";
    }

    await next(context);
});

app.MapDeviceAccess();
app.MapDigitalTwin();
app.MapTwinMedia();
app.MapFirmwareReleases();
app.MapConversations();
app.MapCostumeImages();
app.MapRealtimeTurns();
app.MapRealtimeLive();
app.MapGptLive();
app.MapMemoryAdministration();
app.MapServerModes();
app.MapServerLogs();
app.MapGet("/v1/diagnostics", (HttpContext context, DeviceAccessStore access,
    ConversationStore conversations, GptLiveOptions live, HybridMemoryService memory) =>
{
    if (DeviceAccessEndpoints.Authenticate(context, access) is null)
    {
        return ApiProblem.Create(context, 401, "access_token_required");
    }

    var modes = conversations.ReadServerModes();
    return Results.Ok(new
    {
        voiceMode = modes.VoiceMode,
        dialogMode = modes.DialogMode,
        catalogVersion = PartyWandCatalog.Version,
        audioFormat = "pcm16-mono-32000-wav",
        liveActorMode = live.Mode.ToString(), personalityId = modes.PersonalityId,
        liveDecisionModel = live.DecisionModel, liveTerminalQuietMs = live.TerminalQuietMs,
        characterEnabled = modes.CharacterEnabled, characterTone = modes.CharacterTone,
        characterFinaleProfile = modes.CharacterFinaleProfile, characterVersion = modes.CharacterVersion,
        characterVisualRoast = "permission_pending", characterNamedFinale = "blocked",
        characterAudio = "unsegmented_live_stream", characterEventReady = false,
        memoryEnabled = memory.Options.Enabled, cloudIndexingEnabled = memory.Options.CloudIndexingEnabled,
        longTermPersonalEnabled = memory.Options.LongTermPersonalEnabled,
        memoryProvider = memory.Status
    });
});
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.Run();
