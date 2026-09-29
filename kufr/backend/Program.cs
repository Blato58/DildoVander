using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4096);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DeviceAccessStore>();
builder.Services.AddSingleton<ConversationStore>();

var app = builder.Build();

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
app.Run();
