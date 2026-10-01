using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.VoiceTurns;
using Kufr.Backend.Features.Realtime;
using Kufr.Backend.Features.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4096);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DeviceAccessStore>();
builder.Services.AddSingleton<ServerAdminAccess>();
builder.Services.AddSingleton<LogReadAccess>();
builder.Services.AddSingleton<ServerLogStore>();
builder.Services.AddSingleton<ILoggerProvider, ServerLogProvider>();
builder.Services.AddSingleton<OperationJournal>();
builder.Services.AddSingleton<TextDialogRunner>();
builder.Services.AddSingleton<VoiceProviderRunner>();
builder.Services.AddSingleton<OpenAiRealtimeClient>();
builder.Services.AddSingleton<RealtimeLiveRegistry>();
builder.Services.AddSingleton<EveningMemoryStore>();
builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddHostedService<RetentionWorker>();

var app = builder.Build();
app.Services.GetRequiredService<OperationJournal>();
app.Services.GetRequiredService<TextDialogRunner>();
app.Services.GetRequiredService<VoiceProviderRunner>();
var logs = app.Services.GetRequiredService<ServerLogStore>();
app.Services.GetRequiredService<LogReadAccess>();
logs.Write("info", "server_started");
app.Lifetime.ApplicationStopping.Register(() => logs.Write("info", "server_stopping"));

app.Use((context, next) => ServerLogEvents.RequestAsync(context, next, logs));

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/v1"))
    {
        context.Response.Headers.CacheControl = "no-store";
    }

    await next(context);
});

app.MapDeviceAccess();
app.MapConversations();
app.MapRealtimeTurns();
app.MapRealtimeLive();
app.MapServerModes();
app.MapServerLogs();
app.MapGet("/v1/diagnostics", (HttpContext context, DeviceAccessStore access,
    ConversationStore conversations) =>
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
        audioFormat = "pcm16-mono-32000-wav"
    });
});
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.Run();
