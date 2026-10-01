using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.VoiceTurns;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4096);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DeviceAccessStore>();
builder.Services.AddSingleton<OperationJournal>();
builder.Services.AddSingleton<TextDialogRunner>();
builder.Services.AddSingleton<VoiceProviderRunner>();
builder.Services.AddSingleton<EveningMemoryStore>();
builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddHostedService<RetentionWorker>();

var app = builder.Build();
app.Services.GetRequiredService<OperationJournal>();
app.Services.GetRequiredService<TextDialogRunner>();
app.Services.GetRequiredService<VoiceProviderRunner>();

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
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.Run();
