using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Kufr.Backend.Features.Conversations;

internal sealed class ResponsesTextDialogClient(
    HttpClient httpClient,
    string apiKey,
    string model,
    TimeSpan? requestTimeout = null)
{
    private const int MaxOutputTokens = 1200;
    private const int MaxResponseBytes = 65_536;
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(45);
    private static readonly Uri Endpoint = new("https://api.openai.com/v1/responses");
    private static readonly string Instructions = ReadInstructions();

    public async Task<TextDialogResponse> GenerateAsync(
        string visitorText,
        int readyMask,
        IReadOnlyList<DialogExchange> history,
        bool requireQuestion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model)
            || visitorText is not { Length: > 0 and <= 1000 }
            || readyMask is < 0 or > 63
            || requestTimeout is { } configuredTimeout && configuredTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("Invalid dialogue request or provider configuration.");
        }

        var available = Enumerable.Range(1, 6)
            .Where(number => (readyMask & (1 << (number - 1))) != 0);
        if (history.Count > 2 || history.Any(exchange =>
                exchange.VisitorText.Length > 500 || exchange.Reply.Length > 300))
        {
            throw new ArgumentException("Dialogue history exceeds its bounds.");
        }

        var input = $"Dostupné přihrádky: [{string.Join(", ", available)}]. "
            + "Nejsou-li dostupné žádné, vrať no_selection. "
            + "Katalog: neutrální Hůlka 1 až Hůlka 6. "
            + "Dokončené výměny (odpovědi návštěvníka používej pro závěrečný vtip): "
            + JsonSerializer.Serialize(history, RequestJsonOptions)
            + ". "
            + (requireQuestion
                ? "V tomto tahu polož vlastní navazující otázku; výběr zatím nenavrhuj. "
                : "")
            + $"Text návštěvníka: {visitorText}";
        var requestBody = new
        {
            model,
            store = false,
            reasoning = new { effort = "low" },
            max_output_tokens = MaxOutputTokens,
            instructions = Instructions,
            input,
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "wand_decision",
                    strict = true,
                    schema = ResultSchema(requireQuestion, readyMask)
                }
            }
        };

        var payload = JsonSerializer.Serialize(requestBody, RequestJsonOptions);
        if (Encoding.UTF8.GetByteCount(payload) > ResponsesTextDialogBudget.MaxRequestBytes)
        {
            throw new TextDialogRequestTooLargeException();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(requestTimeout ?? DefaultRequestTimeout);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            deadline.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new TextDialogUnavailableException($"Provider HTTP {(int)response.StatusCode}.");
        }

        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            throw new TextDialogUnavailableException("Provider response is too large.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        var buffer = new byte[MaxResponseBytes + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), deadline.Token);
            if (read == 0)
            {
                break;
            }

            count += read;
        }

        if (count > MaxResponseBytes)
        {
            throw new TextDialogUnavailableException("Provider response is too large.");
        }

        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, count));
            var result = ParseResponse(document.RootElement, readyMask);
            if (requireQuestion && result.Decision == "propose")
            {
                throw new TextDialogUnavailableException("Provider proposed before clarification.");
            }

            return result;
        }
        catch (JsonException)
        {
            throw new TextDialogUnavailableException("Provider response is invalid.");
        }
    }

    internal static TextDialogResponse ParseResponse(JsonElement response, int readyMask)
    {
        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("status", out var status)
            || status.ValueKind != JsonValueKind.String
            || status.GetString() != "completed"
            || !response.TryGetProperty("output", out var output)
            || output.ValueKind != JsonValueKind.Array)
        {
            throw new TextDialogUnavailableException("Provider response is incomplete.");
        }

        string? resultText = null;
        foreach (var item in output.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("type", out var itemType)
                || itemType.ValueKind != JsonValueKind.String)
            {
                throw new TextDialogUnavailableException("Provider output item is invalid.");
            }

            if (itemType.GetString() == "reasoning")
            {
                continue;
            }

            if (itemType.GetString() != "message"
                || item.TryGetProperty("status", out var messageStatus)
                    && (messageStatus.ValueKind != JsonValueKind.String
                        || messageStatus.GetString() != "completed"))
            {
                throw new TextDialogUnavailableException("Provider message is incomplete.");
            }

            if (!item.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
            {
                throw new TextDialogUnavailableException("Provider output is invalid.");
            }

            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object
                    || !part.TryGetProperty("type", out var partType)
                    || partType.ValueKind != JsonValueKind.String)
                {
                    throw new TextDialogUnavailableException("Provider output is invalid.");
                }

                if (partType.GetString() == "refusal")
                {
                    throw new TextDialogUnavailableException("Provider refused the request.");
                }

                if (partType.GetString() != "output_text")
                {
                    throw new TextDialogUnavailableException("Provider output content is invalid.");
                }

                if (resultText is not null || !part.TryGetProperty("text", out var text)
                    || text.ValueKind != JsonValueKind.String)
                {
                    throw new TextDialogUnavailableException("Provider output is ambiguous.");
                }

                resultText = text.GetString();
            }
        }

        if (resultText is null)
        {
            throw new TextDialogUnavailableException("Provider output is missing.");
        }

        using var result = JsonDocument.Parse(resultText);
        var root = result.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 4
            || !TryReadString(root, "reply", 1, 500, out var reply)
            || !TryReadString(root, "reason", 0, 200, out var reason)
            || !TryReadString(root, "decision", 1, 20, out var decision)
            || !root.TryGetProperty("compartment", out var compartmentElement))
        {
            throw new TextDialogUnavailableException("Provider result is invalid.");
        }

        int? compartment = null;
        if (compartmentElement.ValueKind == JsonValueKind.Number
            && compartmentElement.TryGetInt32(out var number))
        {
            compartment = number;
        }
        else if (compartmentElement.ValueKind != JsonValueKind.Null)
        {
            throw new TextDialogUnavailableException("Provider compartment is invalid.");
        }

        if (decision == "propose")
        {
            if (compartment is < 1 or > 6 || compartment is null
                || (readyMask & (1 << (compartment.Value - 1))) == 0)
            {
                throw new TextDialogUnavailableException("Provider proposed an unavailable compartment.");
            }
        }
        else if (decision is not ("ask" or "no_selection") || compartment is not null)
        {
            throw new TextDialogUnavailableException("Provider decision is invalid.");
        }

        if (readyMask == 0 && decision != "no_selection")
        {
            throw new TextDialogUnavailableException("Provider ignored empty inventory.");
        }

        if (!response.TryGetProperty("usage", out var usage)
            || !TryReadNonnegativeInt(usage, "input_tokens", out var inputTokens)
            || !TryReadNonnegativeInt(usage, "output_tokens", out var outputTokens)
            || !TryReadString(response, "model", 1, 128, out var returnedModel))
        {
            throw new TextDialogUnavailableException("Provider usage is invalid.");
        }

        return new TextDialogResponse(
            reply!, decision!, reason!, compartment, returnedModel!, inputTokens, outputTokens);
    }

    private static bool TryReadString(
        JsonElement source,
        string property,
        int minimumLength,
        int maximumLength,
        out string? value)
    {
        value = null;
        if (source.ValueKind != JsonValueKind.Object
            || !source.TryGetProperty(property, out var element)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return value is not null && value.Length >= minimumLength && value.Length <= maximumLength;
    }

    private static bool TryReadNonnegativeInt(JsonElement source, string property, out int value)
    {
        value = 0;
        return source.ValueKind == JsonValueKind.Object
            && source.TryGetProperty(property, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value)
            && value >= 0;
    }

    private static string ReadInstructions()
    {
        using var stream = typeof(ResponsesTextDialogClient).Assembly
            .GetManifestResourceStream("Kufr.Backend.PartyPrompt")
            ?? throw new InvalidOperationException("Embedded party prompt is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Trim();
    }

    private static object ResultSchema(bool requireQuestion, int readyMask) => new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            reply = new { type = "string" },
            decision = new
            {
                type = "string",
                @enum = readyMask == 0
                    ? new[] { "no_selection" }
                    : requireQuestion
                        ? new[] { "ask", "no_selection" }
                        : new[] { "ask", "propose", "no_selection" }
            },
            compartment = new { type = new[] { "integer", "null" }, @enum = new object?[] { 1, 2, 3, 4, 5, 6, null } },
            reason = new { type = "string" }
        },
        required = new[] { "reply", "decision", "compartment", "reason" }
    };
}

internal sealed record TextDialogResponse(
    string Reply,
    string Decision,
    string Reason,
    int? Compartment,
    string ReturnedModel,
    int InputTokens,
    int OutputTokens);

internal sealed record DialogExchange(string VisitorText, string Reply);

internal sealed class TextDialogUnavailableException(string message) : Exception(message);

internal sealed class TextDialogRequestTooLargeException() : Exception("Dialogue request exceeds its reserved size.");
