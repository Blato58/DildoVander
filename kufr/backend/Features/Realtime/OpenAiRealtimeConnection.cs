using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using OpenAI.Realtime;

namespace Kufr.Backend.Features.Realtime;

#pragma warning disable OPENAI002
internal sealed class OpenAiRealtimeConnection : IAsyncDisposable
{
    private readonly WebSocket socket;
    private readonly BackendRealtimeSession session;
    private readonly IAsyncEnumerator<RealtimeServerUpdate> events;
    private readonly HashSet<string> items = new(StringComparer.Ordinal);
    private bool initialized;
    private string? inputItemId;

    internal OpenAiRealtimeConnection(WebSocket socket, CancellationToken token)
    {
        this.socket = socket;
        session = new BackendRealtimeSession(socket);
        events = session.ReceiveUpdatesAsync(token).GetAsyncEnumerator(token);
    }

    internal Task SendAudioAsync(byte[] pcm, CancellationToken token) =>
        session.SendInputAudioAsync(BinaryData.FromBytes(pcm), token);
    internal async Task<RealtimeReply> GenerateTurnAsync(
        RealtimeTurnContext turn,
        byte[]? inputPcm,
        Func<byte[], CancellationToken, Task> audio,
        Func<string, CancellationToken, Task> visitor,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? listening = null,
        Func<string, CancellationToken, Task>? speech = null,
        Func<CancellationToken, Task>? responseReady = null)
    {
        using var abort = cancellationToken.Register(socket.Abort);
        var continuing = initialized;
        if (!initialized)
        {
            if (await NextAsync(cancellationToken) is not RealtimeServerUpdateSessionCreated created
                || created.Session is not RealtimeConversationSession createdSession
                || createdSession.Model != RealtimeProtocol.Model)
            {
                throw new InvalidDataException("Unexpected Realtime session.");
            }
            initialized = true;
        }
        foreach (var itemId in items)
        {
            await session.DeleteItemAsync(itemId, cancellationToken);
        }
        items.Clear();
        if (continuing)
        {
            await session.ClearInputAudioAsync(cancellationToken);
        }
        await session.ConfigureConversationSessionAsync(RealtimeProtocol.SessionOptions(turn), cancellationToken);
        var configuration = await ConfigurationAsync(cancellationToken);
        if (configuration is not RealtimeServerUpdateSessionUpdated updated
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
        if (turn.LiveAudio)
        {
            if (listening is null || speech is null)
            {
                throw new InvalidOperationException("Live input callbacks are required.");
            }
            await listening(cancellationToken);
            for (var count = 0; ; count++)
            {
                if (count >= 2048)
                {
                    throw new InvalidDataException("Too many input events.");
                }
                var update = await NextAsync(cancellationToken);
                if (update is RealtimeServerUpdateInputAudioBufferSpeechStarted)
                {
                    await speech("speech_started", cancellationToken);
                }
                else if (update is RealtimeServerUpdateInputAudioBufferSpeechStopped)
                {
                    await speech("speech_stopped", cancellationToken);
                }
                else if (update is RealtimeServerUpdateInputAudioBufferCommitted committed)
                {
                    inputItemId = ReadString(committed.ItemId, 200);
                    items.Add(inputItemId);
                    // A frame already in flight when VAD committed can
                    // occupy a new buffer. The stopped callback drained
                    // all uploads; remove only this uncommitted tail.
                    await session.ClearInputAudioAsync(cancellationToken);
                    break;
                }
                else if (update is RealtimeServerUpdateError)
                {
                    throw new InvalidDataException("Realtime input failed.");
                }
            }
        }
        else if (inputPcm is not null)
        {
            using var input = new MemoryStream(inputPcm, writable: false);
            await session.SendInputAudioAsync(input, cancellationToken);
            await session.CommitPendingAudioAsync(cancellationToken);
        }
        else if (!turn.Request.Opening)
        {
            await session.AddItemAsync(RealtimeItem.CreateUserMessageItem(turn.Request.Text), cancellationToken);
        }
        if (responseReady is not null)
        {
            await responseReady(cancellationToken);
        }
        await session.StartResponseAsync(cancellationToken);

        var hasAudio = inputPcm is not null || turn.LiveAudio;
        var visitorText = hasAudio ? null : turn.Request.Text;
        var reply = new StringBuilder();
        var decision = "ask";
        var reason = "";
        int? compartment = null;
        string? responseId = null;
        var responses = 0;
        var completed = false;
        var audioBytes = 0;
        var cost = hasAudio ? 10_000L : 0L;
        for (var count = 0; count < 1024 && (!completed || visitorText is null); count++)
        {
            switch (await NextAsync(cancellationToken))
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
                    if (!hasAudio || visitorText is not null
                        || turn.LiveAudio && transcription.ItemId != inputItemId)
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
                    if (response.Usage?.OutputTokenCount > turn.OutputTokenLimit)
                    {
                        throw new InvalidDataException("Output budget exceeded.");
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
                        (decision, reason, compartment) = OpenAiRealtimeClient.ValidateTool(function, turn);
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
    private async Task<RealtimeServerUpdate> ConfigurationAsync(CancellationToken token)
    {
        for (var count = 0; count < 128; count++)
        {
            var update = await NextAsync(token);
            if (update is RealtimeServerUpdateSessionUpdated)
            {
                return update;
            }
            if (update is RealtimeServerUpdateError or RealtimeServerUpdateResponseCreated)
            {
                throw new InvalidDataException("Realtime configuration failed.");
            }
        }
        throw new InvalidDataException("Realtime configuration acknowledgement missing.");
    }

    private async Task<RealtimeServerUpdate> NextAsync(CancellationToken token)
    {
        if (!await events.MoveNextAsync().AsTask().WaitAsync(token))
        {
            throw new InvalidDataException("Realtime stream ended early.");
        }
        var update = events.Current;
        var item = update switch
        {
            RealtimeServerUpdateConversationItemAdded added => added.Item,
            RealtimeServerUpdateConversationItemCreated created => created.Item,
            _ => null
        };
        var id = item switch
        {
            RealtimeMessageItem message => message.Id,
            RealtimeFunctionCallItem function => function.Id,
            RealtimeFunctionCallOutputItem output => output.Id,
            _ => null
        };
        if (id is { Length: > 0 })
        {
            if (items.Count >= 64)
            {
                throw new InvalidDataException("Too many provider conversation items.");
            }
            items.Add(id);
        }
        return update;
    }

    public async ValueTask DisposeAsync()
    {
        socket.Abort();
        try { await events.DisposeAsync(); }
        finally { session.Dispose(); }
    }

    private static string ReadString(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum
            ? value
            : throw new InvalidDataException("Invalid Realtime event field.");
}
#pragma warning restore OPENAI002
