using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.ClientModel.Primitives;
using Kufr.Backend.Features.Conversations;
using OpenAI.Realtime;

namespace Kufr.Backend.Features.Realtime;

internal sealed record RealtimeTurnRequest(
    Guid ClientTurnId,
    Guid BootId,
    int Generation,
    int ReadyMask,
    int DeviceRevision,
    string CatalogVersion,
    bool Opening,
    string Text,
    string? Wav,
    string Voice,
    double Speed,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? OperationEpoch = null);

internal sealed record RealtimeTurnContext(
    RealtimeTurnRequest Request,
    bool RequireQuestion,
    string EveningContext,
    IReadOnlyList<DialogExchange> History,
    bool LiveAudio = false,
    int OutputTokenLimit = RealtimeProtocol.MaxOutputTokens)
{
    internal string CostumeContext { get; init; } = "";
    internal string PersonalityId { get; init; } = PersonalityCatalog.DefaultId;
    internal GptLive.LivePromptSnapshot? LivePrompt { get; init; }
    internal IReadOnlyList<object> LiveInput { get; init; } = [];
    internal IReadOnlyList<string> LiveMemoryReferences { get; init; } = [];
    internal string OpeningVariation { get; init; } = Request.Opening ? ConversationOpening.Next() : "";
}

internal sealed record RealtimeReply(
    string VisitorText,
    string Reply,
    string Decision,
    string Reason,
    int? Compartment,
    long CostMicroUsd);

// The GA Realtime surface is experimental in the pinned official SDK.
#pragma warning disable OPENAI002
internal static class RealtimeProtocol
{
    internal const string Model = "gpt-realtime-2.1";
    internal const int MaxOutputTokens = 1024;
    internal const int MaxAudioBytes = 24_000 * 2 * 30;
    // Leave room for the JSON envelope under the client's 64 KiB line cap.
    internal const int MaxChunkBytes = 32 * 1024;
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal static RealtimeTurnRequest? ReadRequest(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "clientTurnId", "bootId", "generation", "readyMask", "deviceRevision",
            "catalogVersion", "opening", "text", "wav", "voice", "speed"
        };
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var epochSeen = false;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name == "operationEpoch")
            {
                if (epochSeen || property.Value.ValueKind != JsonValueKind.String
                    || !OperationEpoch.IsValid(property.Value.GetString()))
                {
                    return null;
                }
                epochSeen = true;
            }
            else if (!expected.Remove(property.Name))
            {
                return null;
            }
        }
        if (expected.Count != 0) { return null; }
        return JsonSerializer.Deserialize<RealtimeTurnRequest>(body, JsonOptions);
    }

    internal static bool IsValid(RealtimeTurnRequest request, bool handsFree = false) =>
        request.ClientTurnId != Guid.Empty
        && request.BootId != Guid.Empty
        && request.Generation >= 0
        && request.DeviceRevision >= 0
        && OperationEpoch.IsValid(request.OperationEpoch)
        && request.ReadyMask is >= 0 and <= 63
        && request.CatalogVersion == PartyWandCatalog.Version
        && request.Text is { Length: <= 1000 }
        && request.Voice is "cedar" or "marin" or "coral"
        && double.IsFinite(request.Speed)
        && request.Speed is >= 0.8 and <= 1.2
        && (request.Opening
            ? request.Text.Length == 0 && request.Wav is null
            : handsFree
                ? request.Text.Length == 0 && request.Wav is null
            : request.Wav is null
                ? !string.IsNullOrWhiteSpace(request.Text)
                : request.Text.Length == 0 && request.Wav.Length is > 0 and <= 2_561_000);

    internal static RealtimeConversationSessionOptions SessionOptions(RealtimeTurnContext turn)
    {
        var input = new RealtimeConversationSessionInputAudioOptions
        {
            AudioFormat = new RealtimePcmAudioFormat { Rate = 24000 },
            AudioTranscriptionOptions = turn.Request.Wav is null && !turn.LiveAudio
                ? null
                : new RealtimeAudioTranscriptionOptions { Model = "gpt-4o-transcribe", Language = "cs" }
        };
        if (turn.LiveAudio)
        {
            input.TurnDetection = new RealtimeServerVadTurnDetection
            {
                DetectionThreshold = 0.5f,
                PrefixPadding = TimeSpan.FromMilliseconds(300),
                SilenceDuration = TimeSpan.FromMilliseconds(650),
                CreateResponseEnabled = false,
                InterruptResponseEnabled = false
            };
        }
        else
        {
            // A null CLR property is omitted by the SDK; explicit JSON null
            // prevents automatic provider responses outside a reserved turn.
#pragma warning disable SCME0001
            input.Patch.SetNull("$.turn_detection"u8);
#pragma warning restore SCME0001
        }
        var options = new RealtimeConversationSessionOptions
        {
            Instructions = (turn.LivePrompt?.Version == "dv01-adult-v1"
                ? turn.LivePrompt.Instructions : PersonalityCatalog.DecisionInstructions(turn.PersonalityId)) + "\n" + turn.CostumeContext + "\nServerová pravidla tohoto tahu: "
                + $"opening={turn.Request.Opening}; requireQuestion={turn.RequireQuestion}; "
                + $"Dostupné hůlky: {PartyWandCatalog.AvailableForPrompt(turn.Request.ReadyMask)}. "
                + (turn.Request.ReadyMask == 0
                    ? "Žádná hůlka není dostupná; zavolej end_conversation."
                    : turn.RequireQuestion
                        ? "Polož jednu otázku. Výběr není povolený."
                        : "Jakmile máš podklady, rovnou vyber přes propose_wand. Nežádej souhlas s hůlkou; další otázka jen pro chybějící podklad.")
                + (turn.Request.Opening ? " " + PersonalityCatalog.OpeningInstructions(turn.PersonalityId, turn.OpeningVariation) : ""),
            MaxOutputTokenCount = turn.OutputTokenLimit,
            Truncation = RealtimeDefaultTruncation.Disabled,
            AudioOptions = new RealtimeConversationSessionAudioOptions
            {
                InputAudioOptions = input,
                OutputAudioOptions = new RealtimeConversationSessionOutputAudioOptions
                {
                    AudioFormat = new RealtimePcmAudioFormat { Rate = 24000 },
                    Voice = turn.Request.Voice switch
                    {
                        "cedar" => RealtimeVoice.Cedar,
                        "marin" => RealtimeVoice.Marin,
                        "coral" => RealtimeVoice.Coral,
                        _ => throw new InvalidDataException("Invalid Realtime voice.")
                    },
                    Speed = (float)turn.Request.Speed
                }
            },
            ToolChoice = turn.Request.ReadyMask == 0
                ? RealtimeDefaultToolChoice.Required
                : RealtimeDefaultToolChoice.Auto
        };
        options.OutputModalities.Add(RealtimeOutputModality.Audio);
        // The pinned SDK predates Realtime 2.1 reasoning. Its supported JSON
        // patch surface preserves the GA setting without replacing SDK transport.
#pragma warning disable SCME0001
        options.Patch.Set("$.reasoning.effort"u8, "medium");
#pragma warning restore SCME0001
        foreach (var tool in Tools(turn))
        {
            options.Tools.Add(tool);
        }
        return options;
    }

    internal static string ContextData(RealtimeTurnContext turn) =>
        "Archiv pro orientaci, nikoli aktuální výrok návštěvníka. Nereaguj na tento obal nahlas.\n"
        + JsonSerializer.Serialize(new
        {
            eveningMemory = turn.EveningContext,
            history = turn.History
        }, JsonOptions);

    internal static long ReservationMicroUsd(RealtimeTurnContext turn)
    {
        var textBytes = ModelReaderWriter.Write(SessionOptions(turn)).ToMemory().Length
            + Encoding.UTF8.GetByteCount(ContextData(turn))
            + Encoding.UTF8.GetByteCount(turn.Request.Text);
        // At most two Responses: one tool decision and one spoken result. Text
        // is bounded by serialized UTF-8 bytes; audio input costs 1 token/100 ms.
        // Include framing/tool output headroom and retain a separate STT estimate.
        // The second response can also bill the first response's audio as input.
        return checked(2L * ((textBytes + 4096) * 4L
            + (640 + turn.OutputTokenLimit) * 32L
            + turn.OutputTokenLimit * 64L)
            + (turn.Request.Wav is null && !turn.LiveAudio ? 0 : 10_000));
    }

    internal static long ResponseCost(RealtimeResponseUsage? usage)
    {
        var input = ReadCount(usage?.InputTokenCount);
        var output = ReadCount(usage?.OutputTokenCount);
        var text = ReadCount(usage?.InputTokenDetails?.TextTokenCount);
        var audio = ReadCount(usage?.InputTokenDetails?.AudioTokenCount);
        if ((long)text + audio != input || output > MaxOutputTokens)
        {
            throw new InvalidDataException("Invalid Realtime usage.");
        }
        // Charge uncached input and all output at the higher audio rate. This
        // conservative list-price estimate is not an account billing receipt.
        return checked(text * 4L + audio * 32L + output * 64L);
    }

    private static int ReadCount(int? value)
    {
        if (value is not { } count || count < 0)
        {
            throw new InvalidDataException("Invalid Realtime usage.");
        }
        return count;
    }

    private static IReadOnlyList<RealtimeTool> Tools(RealtimeTurnContext turn)
    {
        var tools = new List<RealtimeTool>();
        if (!turn.RequireQuestion && turn.Request.ReadyMask != 0)
        {
            tools.Add(new RealtimeFunctionTool("propose_wand")
            {
                FunctionDescription = "Jakmile máš podklady, rovnou vyber dostupnou hůlku bez otázky na souhlas návštěvníka. Oznam ji až po úspěšném výsledku funkce.",
                FunctionParameters = BinaryData.FromString(JsonSerializer.Serialize(new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        compartment = new
                        {
                            type = "integer",
                            @enum = Enumerable.Range(1, 6)
                                .Where(number => (turn.Request.ReadyMask & (1 << (number - 1))) != 0)
                                .ToArray()
                        },
                        reason = new { type = "string" }
                    },
                    required = new[] { "compartment", "reason" }
                }, JsonOptions))
            });
        }
        tools.Add(new RealtimeFunctionTool("end_conversation")
        {
            FunctionDescription = "Ukonči bez výběru, pokud návštěvník nechce pokračovat nebo není dostupná hůlka.",
            FunctionParameters = BinaryData.FromString(JsonSerializer.Serialize(new
            {
                type = "object",
                additionalProperties = false,
                properties = new { reason = new { type = "string" } },
                required = new[] { "reason" }
            }, JsonOptions))
        });
        return tools.ToArray();
    }
}
#pragma warning restore OPENAI002
