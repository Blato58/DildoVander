using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Usage;
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
        Available = !string.IsNullOrWhiteSpace(key) && CostLimits.Read(configuration).Enabled;
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

    internal async Task<RealtimeReply> GenerateAsync(
        RealtimeTurnContext turn,
        byte[]? inputPcm,
        Func<byte[], CancellationToken, Task> audio,
        Func<string, CancellationToken, Task> visitor,
        CancellationToken cancellationToken)
    {
        if (!Available)
        {
            throw new InvalidOperationException("Realtime is unavailable.");
        }
        var socket = await connect(cancellationToken);
        using var session = new BackendRealtimeSession(socket);
        await using var events = session.ReceiveUpdatesAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            if (await NextAsync(events) is not RealtimeServerUpdateSessionCreated created
                || created.Session is not RealtimeConversationSession createdSession
                || createdSession.Model != RealtimeProtocol.Model)
            {
                throw new InvalidDataException("Unexpected Realtime session.");
            }
            await session.ConfigureConversationSessionAsync(RealtimeProtocol.SessionOptions(turn), cancellationToken);
            if (await NextAsync(events) is not RealtimeServerUpdateSessionUpdated updated
                || updated.Session is not RealtimeConversationSession updatedSession
                || updatedSession.Model != RealtimeProtocol.Model)
            {
                throw new InvalidDataException("Realtime configuration was not accepted.");
            }
            if (turn.EveningContext.Length != 0 || turn.History.Count != 0)
            {
                await session.AddItemAsync(
                    RealtimeItem.CreateUserMessageItem(RealtimeProtocol.ContextData(turn)),
                    cancellationToken);
            }
            if (inputPcm is not null)
            {
                using var input = new MemoryStream(inputPcm, writable: false);
                await session.SendInputAudioAsync(input, cancellationToken);
                await session.CommitPendingAudioAsync(cancellationToken);
            }
            else if (!turn.Request.Opening)
            {
                await session.AddItemAsync(RealtimeItem.CreateUserMessageItem(turn.Request.Text), cancellationToken);
            }
            await session.StartResponseAsync(cancellationToken);

            var visitorText = inputPcm is null ? turn.Request.Text : null;
            var reply = new StringBuilder();
            var decision = "ask";
            var reason = "";
            int? compartment = null;
            string? responseId = null;
            var responses = 0;
            var completed = false;
            var audioBytes = 0;
            var cost = inputPcm is null ? 0L : 10_000L;
            for (var count = 0; count < 1024 && (!completed || visitorText is null); count++)
            {
                switch (await NextAsync(events))
                {
                    case RealtimeServerUpdateResponseCreated responseCreated:
                        if (++responses > 2 || responseId is not null || completed)
                        {
                            throw new InvalidDataException("Unexpected Realtime response.");
                        }
                        responseId = ReadString(responseCreated.Response.Id, 200);
                        break;
                    case RealtimeServerUpdateResponseOutputAudioDelta delta:
                        if (responseId is null || delta.ResponseId != responseId)
                        {
                            throw new InvalidDataException("Unexpected audio response identity.");
                        }
                        var pcm = delta.Delta.ToArray();
                        try
                        {
                            audioBytes = checked(audioBytes + pcm.Length);
                            if (pcm.Length == 0 || pcm.Length % 2 != 0 || audioBytes > RealtimeProtocol.MaxAudioBytes)
                            {
                                throw new InvalidDataException("Invalid Realtime audio length.");
                            }
                            for (var offset = 0; offset < pcm.Length; offset += RealtimeProtocol.MaxChunkBytes)
                            {
                                var chunk = pcm.AsSpan(
                                    offset,
                                    Math.Min(RealtimeProtocol.MaxChunkBytes, pcm.Length - offset)).ToArray();
                                try
                                {
                                    await audio(chunk, cancellationToken);
                                }
                                finally
                                {
                                    CryptographicOperations.ZeroMemory(chunk);
                                }
                            }
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(pcm);
                        }
                        break;
                    case RealtimeServerUpdateConversationItemInputAudioTranscriptionCompleted transcription:
                        if (inputPcm is null || visitorText is not null)
                        {
                            throw new InvalidDataException("Unexpected input transcription.");
                        }
                        visitorText = ReadString(transcription.Transcript, 1000);
                        await visitor(visitorText, cancellationToken);
                        break;
                    case RealtimeServerUpdateResponseDone done:
                        var response = done.Response;
                        if (responseId is null || response.Id != responseId
                            || response.Status != RealtimeResponseStatus.Completed)
                        {
                            throw new InvalidDataException("Incomplete Realtime response.");
                        }
                        cost = checked(cost + RealtimeProtocol.ResponseCost(response.Usage));
                        responseId = null;
                        RealtimeFunctionCallItem? function = null;
                        foreach (var output in response.OutputItems)
                        {
                            switch (output)
                            {
                                case RealtimeMessageItem message:
                                    foreach (var content in message.Content)
                                    {
                                        if (content is not RealtimeOutputAudioMessageContentPart spoken)
                                        {
                                            throw new InvalidDataException("Unexpected Realtime output modality.");
                                        }
                                        reply.Append(ReadString(spoken.Transcript, 500));
                                    }
                                    break;
                                case RealtimeFunctionCallItem call:
                                    if (function is not null || responses != 1)
                                    {
                                        throw new InvalidDataException("Unexpected Realtime tool count.");
                                    }
                                    function = call;
                                    break;
                                default:
                                    throw new InvalidDataException("Unsupported Realtime output.");
                            }
                        }
                        if (reply.Length > 500)
                        {
                            throw new InvalidDataException("Realtime reply is too long.");
                        }
                        if (function is not null)
                        {
                            (decision, reason, compartment) = ValidateTool(function, turn);
                            var toolResult = JsonSerializer.Serialize(new
                            {
                                accepted = true,
                                decision,
                                compartment,
                                availableWands = PartyWandCatalog.AvailableForPrompt(turn.Request.ReadyMask),
                                instruction = "Řekni krátký závěr s jednou pointou. Toto je návrh, pohyb zatím neproběhl."
                            }, RealtimeProtocol.JsonOptions);
                            await session.AddItemAsync(
                                RealtimeItem.CreateFunctionCallOutputItem(ReadString(function.CallId, 200), toolResult),
                                cancellationToken);
                            await session.StartResponseAsync(
                                new RealtimeResponseOptions { ToolChoice = RealtimeDefaultToolChoice.None },
                                cancellationToken);
                        }
                        else
                        {
                            if (reply.Length == 0 || audioBytes == 0
                                || turn.Request.ReadyMask == 0 && decision != "no_selection")
                            {
                                throw new InvalidDataException("Realtime reply is missing.");
                            }
                            completed = true;
                        }
                        break;
                    case RealtimeServerUpdateError:
                    case RealtimeServerUpdateConversationItemInputAudioTranscriptionFailed:
                        // Provider messages may contain visitor text. Never expose them.
                        throw new InvalidDataException("Realtime provider failed.");
                }
            }
            if (!completed || visitorText is null)
            {
                throw new InvalidDataException("Realtime turn did not complete.");
            }
            return new(visitorText, reply.ToString(), decision, reason, compartment, cost);
        }
        finally
        {
            // Each PTT turn has a bounded provider session. Abort also cancels
            // generation after Stop, disconnect or the common deadline.
            socket.Abort();
        }
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

    private static async Task<RealtimeServerUpdate> NextAsync(IAsyncEnumerator<RealtimeServerUpdate> events)
    {
        if (!await events.MoveNextAsync())
        {
            throw new InvalidDataException("Realtime stream ended early.");
        }
        return events.Current;
    }

    private static string ReadString(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum
            ? value
            : throw new InvalidDataException("Invalid Realtime event field.");
}
#pragma warning restore OPENAI002
