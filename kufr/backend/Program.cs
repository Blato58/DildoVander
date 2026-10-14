using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.VoiceTurns;
using Kufr.Backend.Features.Realtime;
using Kufr.Backend.Features.Diagnostics;
using Kufr.Backend.Features.Memory;
using Kufr.Backend.Features.Firmware;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using System.Threading.RateLimiting;
using Kufr.Backend.Features.Keepsakes;

var provisioning = args.Contains("--memory-provision");
var builder = WebApplication.CreateBuilder(args.Where(a => a is not ("--memory-provision" or "--execute" or "--create")).ToArray());
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4096);
builder.Services.AddSingleton(TimeProvider.System);
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
    options.AddPolicy("keepsake", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("keepsake-public", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
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
var keepsakesEnabled = builder.Configuration.GetValue<bool>("Keepsake:Enabled");
var keepsakeDataRoot = builder.Configuration["Keepsake:DataRoot"];
var keepsakeArchiveConfigured = !string.IsNullOrWhiteSpace(keepsakeDataRoot);
if (keepsakesEnabled && !keepsakeArchiveConfigured)
{
    throw new InvalidOperationException("Keepsake:DataRoot must be configured.");
}
if (keepsakeArchiveConfigured)
{
    var dataRoot = keepsakeDataRoot!;
    builder.Services.AddDataProtection().SetApplicationName("DildoVander.DV10")
        .PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["Keepsake:DataProtectionRoot"] ?? dataRoot + ".keys"));
    builder.Services.AddSingleton<KeepsakeStore>();
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
        options.ForwardLimit = 1;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        options.KnownProxies.Add(System.Net.IPAddress.Loopback);
        options.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
    });
}
if (keepsakesEnabled && builder.Configuration.GetValue<bool>("Keepsake:GenerationEnabled"))
{
    builder.Services.AddSingleton(sp => KeepsakeProviderFactory.Create(sp.GetRequiredService<IConfiguration>()));
    builder.Services.AddHostedService<KeepsakeWorker>();
}
if (keepsakesEnabled) { builder.Services.AddHostedService<KeepsakeExportWorker>(); }

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
// A configured private archive remains an authority even while guest production is
// disabled. Resolve it eagerly so confirmed deletion cleanup resumes before serving.
if (keepsakeArchiveConfigured) { app.Services.GetRequiredService<KeepsakeStore>(); }
if (keepsakesEnabled) { app.Services.GetRequiredService<EveningMemoryStore>(); }
app.Services.GetRequiredService<TextDialogRunner>();
app.Services.GetRequiredService<VoiceProviderRunner>();
var logs = app.Services.GetRequiredService<ServerLogStore>();
app.Services.GetRequiredService<LogReadAccess>();
logs.Write("info", "server_started");
app.Lifetime.ApplicationStopping.Register(() => logs.Write("info", "server_stopping"));

app.Use((context, next) => ServerLogEvents.RequestAsync(context, next, logs));
if (keepsakeArchiveConfigured) { app.UseForwardedHeaders(); }
app.UseRateLimiter();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/v1") || context.Request.Path.StartsWithSegments("/guest")
        || context.Request.Path.StartsWithSegments("/keepsake"))
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    await next(context);
});

app.MapDeviceAccess();
app.MapFirmwareReleases();
app.MapConversations();
app.MapCostumeImages();
app.MapRealtimeTurns();
app.MapRealtimeLive();
app.MapGptLive();
app.MapMemoryAdministration();
app.MapServerModes();
app.MapServerLogs();
if (keepsakesEnabled) { app.MapKeepsakes(); app.MapKeepsakeGallery(); }
if (keepsakeArchiveConfigured) { app.MapKeepsakeAdministration(); }
app.Map("/guest/{**path}", () => Results.Json(new { errorCode = "access_unavailable" }, statusCode: 404));
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
