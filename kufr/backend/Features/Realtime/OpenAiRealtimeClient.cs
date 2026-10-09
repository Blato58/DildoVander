using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using OpenAI.Realtime;

namespace Kufr.Backend.Features.Realtime;

#pragma warning disable OPENAI002
internal sealed class OpenAiRealtimeClient : IDisposable
{
    private readonly Func<CancellationToken, Task<WebSocket>> connect;
    private readonly HttpMessageInvoker? transport;

    public OpenAiRealtimeClient(IConfiguration configuration)
    {
        var key = configuration["OPENAI_API_KEY"];
        Available = !string.IsNullOrWhiteSpace(key);
        transport = new HttpMessageInvoker(new HttpClientHandler { AllowAutoRedirect = false });
        connect = async token =>
        {
            var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol("realtime");
            socket.Options.SetRequestHeader("Authorization", "Bearer " + key);
            try
            {
                // Keep the credential on the fixed provider endpoint. The SDK
                // session accepts this bounded, no-redirect transport.
                await socket.ConnectAsync(
                    new Uri("wss://api.openai.com/v1/realtime?model=" + RealtimeProtocol.Model),
                    transport,
                    token);
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
    }

    internal OpenAiRealtimeClient(Func<CancellationToken, Task<WebSocket>> connect)
    {
        this.connect = connect;
        Available = true;
    }

    internal bool Available { get; }
    public void Dispose() => transport?.Dispose();

    internal async Task<OpenAiRealtimeConnection> ConnectAsync(CancellationToken token)
    {
        if (!Available)
        {
            throw new InvalidOperationException("Realtime is unavailable.");
        }
        return new OpenAiRealtimeConnection(await connect(token), token);
    }

    internal async Task<RealtimeReply> GenerateAsync(
        RealtimeTurnContext turn,
        byte[]? inputPcm,
        Func<byte[], CancellationToken, Task> audio,
        Func<string, CancellationToken, Task> visitor,
        CancellationToken cancellationToken)
    {
        await using var connection = await ConnectAsync(cancellationToken);
        return await connection.GenerateTurnAsync(turn, inputPcm, audio, visitor, cancellationToken);
    }
    internal static (string Decision, string Reason, int? Compartment) ValidateTool(
        RealtimeFunctionCallItem call,
        RealtimeTurnContext turn)
    {
        if (call.FunctionArguments.ToMemory().Length > 4096)
        {
            throw new InvalidDataException("Invalid Realtime tool arguments.");
        }
        using var arguments = JsonDocument.Parse(call.FunctionArguments.ToMemory());
        var root = arguments.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("reason", out var reasonElement)
            || reasonElement.ValueKind != JsonValueKind.String
            || reasonElement.GetString() is not { Length: <= 200 } reason)
        {
            throw new InvalidDataException("Invalid Realtime tool arguments.");
        }
        if (call.FunctionName == "end_conversation"
            && root.EnumerateObject().Select(property => property.Name).SequenceEqual(["reason"]))
        {
            return ("no_selection", reason, null);
        }
        if (call.FunctionName != "propose_wand" || turn.RequireQuestion
            || root.EnumerateObject().Count() != 2
            || !root.TryGetProperty("compartment", out var selected)
            || !selected.TryGetInt32(out var number)
            || number is < 1 or > 6
            || (turn.Request.ReadyMask & (1 << (number - 1))) == 0)
        {
            throw new InvalidDataException("Realtime selection was rejected.");
        }
        return ("propose", reason, number);
    }

}
#pragma warning restore OPENAI002
