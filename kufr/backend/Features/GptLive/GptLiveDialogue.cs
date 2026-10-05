using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Realtime;

namespace Kufr.Backend.Features.GptLive;

internal sealed record LiveTranscript(string Visitor, string Reply);
internal sealed record LiveOutcome(RealtimeReply Reply, int Questions, IReadOnlyList<LiveTranscript> Transcript);

// One event reader remains active while a client delegation runs through Sol.
// Neither transcript packet gaps nor a delegation completion represent audio-done.
internal sealed class GptLiveDialogue(GptLiveConnection connection, TextDialogRunner dialog)
{
    internal async Task<LiveOutcome> RunAsync(RealtimeTurnContext turn,
        Func<byte[], CancellationToken, Task> audio,
        Func<string, CancellationToken, Task> visitor,
        Func<CancellationToken, Task> pause,
        CancellationToken token,
        Func<CancellationToken, Task>? stopInput = null)
    {
        await connection.SendAsync(GptLiveProtocol.Start(turn), token);
        using (var first = await connection.ReceiveAsync(token))
        {
            if (GptLiveProtocol.RequiredString(first.RootElement, "type", 100) != "session.started"
                || first.RootElement.GetProperty("session").GetProperty("model").GetString() != GptLiveProtocol.Model)
                throw new InvalidDataException("Live startup failed.");
        }
        connection.AudioReady();
        await connection.SendAsync(new { type = "session.instructions.append", delegation_id = (string?)null,
            content = turn.Request.ReadyMask == 0
                ? "Žádná hůlka není dostupná. Úvodní otázku nepokládej; promluv až po dodání závěrečné repliky."
                : turn.PersonalityId == PersonalityCatalog.DefaultId
                    ? "Začni teď vlastním krátkým oslovením a první otevřenou otázkou podle pravidel nového rozhovoru."
                    : "Začni teď krátkým přivítáním, rýpnutím a jednou otázkou podle zvolené personality." }, token);
        var elapsed = Stopwatch.StartNew();
        var input = new StringBuilder();
        var archive = new List<LiveTranscript>();
        var delegations = new HashSet<string>(StringComparer.Ordinal);
        Task<TextDialogResponse>? pending = null;
        Task<JsonDocument>? receive = null;
        string? pendingId = null;
        string? deferredId = null;
        var pendingInput = "";
        var version = 0;
        var pendingVersion = 0;
        var lastInput = TimeSpan.Zero;
        var questions = 1; // the independent fresh-opening question
        var modelCost = 0L;
        var backendCalls = 0;
        var lastUsage = 0.0;
        TextDialogResponse? terminal = null;
        var terminalAt = TimeSpan.Zero;
        var terminalSpeech = false;
        var terminalTranscript = false;
        var lastSpeech = TimeSpan.Zero;
        var spokeSincePause = false;
        var closing = false;
        var closeAt = TimeSpan.Zero;
        var bytes = 0;
        var transcriptCharacters = 0;
        using var backendWork = CancellationTokenSource.CreateLinkedTokenSource(token);
        void StartBackend(string? id)
        {
            var reservation = ResponsesTextDialogBudget.ReserveForContext(turn.EveningContext, personalityId: turn.PersonalityId);
            if (++backendCalls > 10
                || modelCost + reservation + GptLiveProtocol.VoiceReservation > GptLiveProtocol.SessionBudget)
                throw new InvalidDataException("Live session budget exhausted.");
            pendingId = id;
            deferredId = null;
            pendingVersion = version;
            pendingInput = input.ToString();
            pending = dialog.GenerateAsync(pendingInput, turn.Request.ReadyMask, SpokenHistory(archive),
                questions < PersonalityCatalog.MinimumQuestions(turn.PersonalityId), backendWork.Token, turn.EveningContext, turn.PersonalityId);
        }
        if (turn.Request.ReadyMask == 0)
            StartBackend(null);
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                receive ??= connection.ReceiveAsync(token);
                // This timer detects output PCM silence for playback segmentation,
                // not a completed conversational turn. Only session.closed finalizes.
                var tick = Task.Delay(50, token);
                // A fast Sol completion can overtake the rest of a spoken
                // answer. Read deltas before delivering or recomputing it.
                // This transcript gap is a heuristic, not provider VAD.
                var inputSettled = elapsed.Elapsed - lastInput >= TimeSpan.FromMilliseconds(650);
                var winner = pending is null || !inputSettled ? await Task.WhenAny(receive, tick)
                    : await Task.WhenAny(receive, pending, tick);
                if (pending is not null && winner == pending)
                {
                    var result = await pending;
                    pending = null;
                    modelCost = checked(modelCost + ResponsesTextDialogBudget.CalculateActualMicroUsd(result));
                    if (version != pendingVersion)
                    {
                        // Recompute the changed input ourselves. Depending on another
                        // model delegation here can leave the call waiting forever.
                        StartBackend(deferredId ?? pendingId);
                        continue;
                    }
                    if (result.Decision == "ask") questions++;
                    else { terminal = result; terminalAt = elapsed.Elapsed; }
                    input.Clear();
                    deferredId = null;
                    if (terminal is not null)
                        await connection.SendAsync(new { type = "session.instructions.append", delegation_id = pendingId,
                            content = "Následující dodaná replika je závěrečná. Vyslov ji a pak už mlč; rozhovor končí." }, token);
                    // Appended instructions may interrupt speech: steer before
                    // delivering the commentary, never immediately after it.
                    await connection.SendAsync(new { type = "session.commentary.append", delegation_id = pendingId,
                        content = result.Reply }, token);
                    continue;
                }
                if (winner == receive)
                {
                    using var document = await receive;
                    receive = null;
                    var value = document.RootElement;
                    switch (GptLiveProtocol.RequiredString(value, "type", 100))
                    {
                        case "error": throw new InvalidDataException("Live command failed.");
                        case "session.output_audio.delta":
                            var pcm = Convert.FromBase64String(GptLiveProtocol.RequiredString(value, "delta", 180_000));
                            try
                            {
                                bytes = checked(bytes + pcm.Length);
                                if (pcm.Length == 0 || pcm.Length % 2 != 0 || bytes > 24_000 * 2 * 190)
                                    throw new InvalidDataException("Invalid Live audio.");
                                if (GptLiveProtocol.HasSpeech(pcm))
                                {
                                    lastSpeech = elapsed.Elapsed;
                                    spokeSincePause = true;
                                    if (terminal is not null) terminalSpeech = true;
                                }
                                // Silence is used for output VAD, not queued in the speaker.
                                if (spokeSincePause)
                                    for (var offset = 0; offset < pcm.Length; offset += RealtimeProtocol.MaxChunkBytes)
                                    {
                                        var chunk = pcm.AsSpan(offset, Math.Min(RealtimeProtocol.MaxChunkBytes, pcm.Length - offset)).ToArray();
                                        try { await audio(chunk, token); }
                                        finally { CryptographicOperations.ZeroMemory(chunk); }
                                    }
                            }
                            finally { CryptographicOperations.ZeroMemory(pcm); }
                            break;
                        case "session.input_transcript.delta":
                        case "session.output_transcript.delta":
                            var isInput = value.GetProperty("type").GetString() == "session.input_transcript.delta";
                            var fragment = GptLiveProtocol.RequiredString(value, "delta", 1000);
                            if ((transcriptCharacters += fragment.Length) > 32_000)
                                throw new InvalidDataException("Live transcript too large.");
                            if (isInput)
                            {
                                if (input.Length + fragment.Length > 1000) throw new InvalidDataException("Live answer too long.");
                                input.Append(fragment); version++;
                                lastInput = elapsed.Elapsed;
                                if (terminal is not null)
                                {
                                    // A correction before finalization invalidates the pending
                                    // selection. No proposal has been published to a device yet.
                                    terminal = null; terminalSpeech = terminalTranscript = false;
                                    if (closing) throw new InvalidDataException("Live selection interrupted during close.");
                                    await connection.SendAsync(new { type = "session.instructions.append", delegation_id = (string?)null,
                                        content = "Návštěvník doplnil odpověď. Předchozí závěr už neplatí; backend připravuje novou repliku. Poslouchej bez další otázky." }, token);
                                    deferredId = pendingId;
                                }
                                AddTranscript(archive, true, fragment);
                                await visitor(fragment, token);
                                // A delegation can precede its transcript on the
                                // wire. Retain it until text arrives rather than
                                // asking the voice model to try again.
                                if (pending is null && deferredId is not null && !string.IsNullOrWhiteSpace(input.ToString()))
                                    StartBackend(deferredId);
                            }
                            else
                            {
                                AddTranscript(archive, false, fragment);
                                if (terminal is not null) terminalTranscript = true;
                            }
                            break;
                        case "session.delegation.created":
                            var delegation = value.GetProperty("delegation");
                            var id = GptLiveProtocol.RequiredString(delegation, "id", 200);
                            if (GptLiveProtocol.RequiredString(delegation, "target", 20) != "client"
                                || !delegations.Add(id) || delegations.Count > 10)
                                throw new InvalidDataException("Invalid Live delegation.");
                            if (closing || terminal is not null) break;
                            if (pending is not null)
                            {
                                // A duplicate at the same revision creates neither
                                // a paid call nor a spoken waiting notification.
                                if (version != pendingVersion) deferredId = id;
                                break;
                            }
                            if (string.IsNullOrWhiteSpace(input.ToString())) deferredId = id;
                            else StartBackend(id);
                            break;
                        case "session.usage.updated":
                        case "session.closed":
                            var seconds = value.GetProperty("usage").GetProperty("seconds").GetDouble();
                            _ = GptLiveProtocol.VoiceCost(seconds);
                            if (seconds < lastUsage) throw new InvalidDataException("Live usage regressed.");
                            lastUsage = seconds;
                            if (value.GetProperty("type").GetString() == "session.closed")
                            {
                                if (!closing || terminal is null || value.GetProperty("reason").GetString() != "close_requested")
                                    throw new InvalidDataException("Live ended unexpectedly.");
                                return new(new(pendingInput, terminal.Reply, terminal.Decision, terminal.Reason,
                                    terminal.Compartment, modelCost + GptLiveProtocol.VoiceCost(seconds)), questions, archive);
                            }
                            break;
                        // Context acknowledgments do not indicate completed speech.
                        case "session.instructions.appended":
                        case "session.thinking.appended":
                        case "session.commentary.appended":
                        case "info": break;
                        default: throw new InvalidDataException("Unsupported Live event.");
                    }
                }
                if (spokeSincePause && elapsed.Elapsed - lastSpeech >= TimeSpan.FromMilliseconds(900))
                {
                    spokeSincePause = false;
                    await pause(token);
                    if (terminal is not null && terminalSpeech && terminalTranscript && !closing)
                    {
                        // Shut the microphone gate before waiting for final
                        // usage; discard late uploads instead of queuing them.
                        if (stopInput is not null) await stopInput(token);
                        await connection.CloseAsync(token);
                        closing = true; closeAt = elapsed.Elapsed;
                    }
                }
                if (closing && elapsed.Elapsed - closeAt > TimeSpan.FromSeconds(5)
                    || terminal is not null && !closing && elapsed.Elapsed - terminalAt > TimeSpan.FromSeconds(20))
                    throw new InvalidDataException("Live finalization timed out.");
            }
        }
        finally
        {
            backendWork.Cancel();
            if (pending is not null) { try { await pending; } catch (Exception) { } }
            // The endpoint disposes the socket; observe the outstanding receive
            // through its lifetime cancellation before releasing the reservation.
            if (receive is not null) _ = receive.ContinueWith(task =>
            {
                if (task.IsCompletedSuccessfully) task.Result.Dispose();
                else _ = task.Exception;
            }, TaskContinuationOptions.ExecuteSynchronously);
        }
    }

    private static void AddTranscript(List<LiveTranscript> archive, bool visitor, string fragment)
    {
        // Provider deltas can divide a word. Coalesce adjacent same-speaker
        // fragments before persistence/recall, retaining the store's row bounds.
        var maximum = visitor ? 1000 : 500;
        while (fragment.Length > 0)
        {
            var last = archive.Count > 0 ? archive[^1] : null;
            var sameSpeaker = last is not null && (visitor ? last.Reply.Length == 0 : last.Visitor.Length == 0);
            var existing = sameSpeaker ? (visitor ? last!.Visitor : last!.Reply) : "";
            if (!sameSpeaker || existing.Length == maximum)
            {
                archive.Add(new("", ""));
                existing = "";
            }
            var count = Math.Min(maximum - existing.Length, fragment.Length);
            var joined = existing + fragment[..count];
            archive[^1] = visitor ? new(joined, "") : new("", joined);
            fragment = fragment[count..];
        }
    }

    private static IReadOnlyList<DialogExchange> SpokenHistory(IReadOnlyList<LiveTranscript> archive)
    {
        var history = new List<DialogExchange>();
        var visitor = "";
        foreach (var item in archive)
        {
            if (item.Visitor.Length > 0) visitor = item.Visitor;
            if (item.Reply.Length == 0) continue;
            history.Add(new(visitor[..Math.Min(visitor.Length, 500)], item.Reply[..Math.Min(item.Reply.Length, 300)]));
            if (history.Count > 2) history.RemoveAt(0);
            visitor = "";
        }
        return history;
    }

}
