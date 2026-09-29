using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kufr.Backend.Features.Conversations;

internal static class ResponsesTextDialogClientTests
{
    public static async Task RunAsync()
    {
        string? capturedRequest = null;
        var validHandler = new StubHandler(async (request, cancellationToken) =>
        {
            capturedRequest = await request.Content!.ReadAsStringAsync(cancellationToken);
            Check(request.RequestUri?.ToString() == "https://api.openai.com/v1/responses", "fixed endpoint");
            Check(request.Headers.Authorization?.Scheme == "Bearer", "bearer scheme");
            Check(request.Headers.Authorization?.Parameter == "test-key", "bearer key");
            return JsonResponse(ProviderResponse("propose", 5));
        });
        using var validHttp = new HttpClient(validHandler);
        var validClient = new ResponsesTextDialogClient(validHttp, "test-key", "gpt-6.1-sol");
        var history = new[]
        {
            new DialogExchange("Chci chaos.", "Jaký chaos?"),
            new DialogExchange("Vtipný a třpytivý.", "Jak moc třpytu?")
        };
        var proposed = await validClient.GenerateAsync("Hodně.", 16, history, false, CancellationToken.None);
        Check(proposed is
        {
            Decision: "propose",
            Compartment: 5,
            InputTokens: 621,
            OutputTokens: 102,
            ReturnedModel: "gpt-6.1-sol"
        }, "valid structured proposal");
        using (var request = JsonDocument.Parse(capturedRequest!))
        {
            var root = request.RootElement;
            Check(root.GetProperty("service_tier").GetString() == "default", "explicit Standard tier");
            Check(root.GetProperty("store").ValueKind == JsonValueKind.False, "store disabled");
            Check(root.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean(),
                "strict structured output");
            Check(root.GetProperty("instructions").GetString()!.Contains("fyzické potvrzení", StringComparison.Ordinal),
                "shared party prompt embedded");
            Check(root.GetProperty("instructions").GetString()!.Contains("Ollivanderova obchodu", StringComparison.Ordinal),
                "shared prompt establishes magical suitcase role");
            Check(root.GetProperty("instructions").GetString()!.Contains("Nepředstavuj se znovu", StringComparison.Ordinal),
                "host introduces the suitcase, model must not repeat it");
            Check(root.GetProperty("input").GetString()!.Contains("[5]", StringComparison.Ordinal),
                "ready mask limits provider input");
            Check(root.GetProperty("input").GetString()!.Contains("Kaštanová hůlka Kocovina věštců", StringComparison.Ordinal),
                "available catalog entry is passed to provider");
            Check(!root.GetProperty("input").GetString()!.Contains("Jasanová hůlka Poslední slovo", StringComparison.Ordinal),
                "unavailable catalog entry is excluded from provider input");
            Check(root.GetProperty("input").GetString()!.Contains("Vtipný a třpytivý.", StringComparison.Ordinal),
                "completed visitor answer is passed to provider");
        }

        string? questionRequest = null;
        using (var questionHttp = new HttpClient(new StubHandler(async (request, token) =>
        {
            questionRequest = await request.Content!.ReadAsStringAsync(token);
            return JsonResponse(ProviderResponse("ask", null));
        })))
        {
            var questionClient = new ResponsesTextDialogClient(questionHttp, "test-key", "gpt-6.1-sol");
            var question = await questionClient.GenerateAsync("Chci drama.", 16, [], true,
                CancellationToken.None);
            Check(question.Decision == "ask", "provider generates the clarification turn");
        }
        using (var request = JsonDocument.Parse(questionRequest!))
        {
            var root = request.RootElement;
            var decisions = root.GetProperty("text").GetProperty("format")
                .GetProperty("schema").GetProperty("properties")
                .GetProperty("decision").GetProperty("enum");
            Check(decisions.EnumerateArray().All(item => item.GetString() != "propose"),
                "early-turn schema excludes proposals");
            Check(root.GetProperty("input").GetString()!.Contains("polož vlastní navazující otázku",
                    StringComparison.Ordinal),
                "model is asked to generate its own clarification");
        }

        using (var openingHttp = new HttpClient(new StubHandler(async (request, token) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var input = document.RootElement.GetProperty("input").GetString()!;
            Check(input.Contains("Událost: opening", StringComparison.Ordinal), "explicit opening event");
            Check(!input.Contains("Text návštěvníka:", StringComparison.Ordinal), "no fabricated visitor text");
            Check(input.Contains("Petr a taxik", StringComparison.Ordinal), "evening memory reaches provider");
            return JsonResponse(ProviderResponse("ask", null));
        })))
        {
            var openingClient = new ResponsesTextDialogClient(openingHttp, "test-key", "gpt-6.1-sol");
            await openingClient.GenerateAsync("", 63, [], true, CancellationToken.None, "Petr a taxik");
        }

        using (var earlyHttp = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(JsonResponse(ProviderResponse("propose", 5))))))
        {
            var earlyClient = new ResponsesTextDialogClient(earlyHttp, "test-key", "gpt-6.1-sol");
            try
            {
                await earlyClient.GenerateAsync("Chci drama.", 16, [], true, CancellationToken.None);
                throw new Exception("Early proposal was accepted.");
            }
            catch (TextDialogUnavailableException)
            {
            }
        }

        await ExpectUnavailableAsync(ProviderResponse("propose", 6), 16, "unavailable compartment");
        await ExpectUnavailableAsync(ProviderResponse("ask", 5), 16, "non-proposal compartment");
        await ExpectUnavailableAsync(ProviderResponse("propose", 5, status: "incomplete"), 16,
            "incomplete response");
        await ExpectUnavailableAsync(ProviderResponse("ask", null), 0,
            "empty inventory requires no selection");
        await ExpectUnavailableAsync("{invalid", 16, "malformed response");
        await ExpectUnavailableAsync("{\"status\":\"completed\",\"output\":[42]}", 16,
            "malformed output item");
        await ExpectUnavailableAsync(
            "{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"refusal\"}]}]}",
            16,
            "explicit refusal");
        await ExpectUnavailableAsync(ProviderResponse("propose", 5, outputTokens: -1), 16,
            "invalid usage");
        foreach (var tier in new string?[] { null, "fast", "priority", "unknown" })
        {
            var wrongTier = JsonNode.Parse(ProviderResponse("propose", 5))!;
            if (tier is null)
            {
                wrongTier.AsObject().Remove("service_tier");
            }
            else
            {
                wrongTier["service_tier"] = tier;
            }
            await ExpectUnavailableAsync(wrongTier.ToJsonString(), 16, "unconfirmed Standard tier");
        }
        var mixedResponse = JsonNode.Parse(ProviderResponse("propose", 5))!;
        mixedResponse["output"]![0]!["content"]!.AsArray().Add(new JsonObject
        {
            ["type"] = "refusal"
        });
        await ExpectUnavailableAsync(mixedResponse.ToJsonString(), 16, "mixed text and refusal");

        var incompleteMessage = JsonNode.Parse(ProviderResponse("propose", 5))!;
        incompleteMessage["output"]![0]!["status"] = "incomplete";
        await ExpectUnavailableAsync(incompleteMessage.ToJsonString(), 16, "incomplete message");

        await ExpectUnavailableAsync(new string('x', 65_537), 16, "oversized body");

        using var neverSentHttp = new HttpClient(new StubHandler((_, _) =>
            throw new Exception("Oversized request reached the provider.")));
        var boundedClient = new ResponsesTextDialogClient(neverSentHttp, "test-key", "gpt-6.1-sol");
        try
        {
            var oversizedHistory = new[]
            {
                new DialogExchange(new string('\u0001', 500), new string('\u0001', 300)),
                new DialogExchange(new string('\u0001', 500), new string('\u0001', 300))
            };
            await boundedClient.GenerateAsync(new string('\u0001', 1000), 1,
                oversizedHistory, false, CancellationToken.None);
            throw new Exception("Oversized request passed the reservation boundary.");
        }
        catch (TextDialogRequestTooLargeException)
        {
        }

        using var stalledHttp = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BlockingReadStream())
            })));
        var stalledClient = new ResponsesTextDialogClient(
            stalledHttp,
            "test-key",
            "gpt-6.1-sol",
            TimeSpan.FromMilliseconds(50));
        try
        {
            await stalledClient.GenerateAsync("Test", 1, [], false, CancellationToken.None);
            throw new Exception("Stalled body passed the deadline.");
        }
        catch (OperationCanceledException)
        {
        }

        using var failedHttp = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("private provider error")
            })));
        var failedClient = new ResponsesTextDialogClient(failedHttp, "test-key", "gpt-6.1-sol");
        try
        {
            await failedClient.GenerateAsync("Test", 1, [], false, CancellationToken.None);
            throw new Exception("Provider HTTP error was accepted.");
        }
        catch (TextDialogUnavailableException error)
        {
            Check(!error.Message.Contains("private", StringComparison.Ordinal), "provider body stays private");
        }
    }

    private static async Task ExpectUnavailableAsync(string response, int readyMask, string label)
    {
        using var http = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(JsonResponse(response))));
        var client = new ResponsesTextDialogClient(http, "test-key", "gpt-6.1-sol");
        try
        {
            await client.GenerateAsync("Test", readyMask, [], false, CancellationToken.None);
            throw new Exception($"Failed: {label}");
        }
        catch (TextDialogUnavailableException)
        {
        }
    }

    private static string ProviderResponse(
        string decision,
        int? compartment,
        string status = "completed",
        int outputTokens = 102)
    {
        var result = JsonSerializer.Serialize(new
        {
            reply = "Navrhuji Hůlku 5.",
            decision,
            compartment,
            reason = "Hravá volba"
        });
        return JsonSerializer.Serialize(new
        {
            model = "gpt-6.1-sol",
            service_tier = "default",
            status,
            output = new[]
            {
                new
                {
                    type = "message",
                    content = new[] { new { type = "output_text", text = result } }
                }
            },
            usage = new { input_tokens = 621, output_tokens = outputTokens }
        });
    }

    private static HttpResponseMessage JsonResponse(string content)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
    }

    private static void Check(bool condition, string label)
    {
        if (!condition)
        {
            throw new Exception($"Failed: {label}");
        }
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return respond(request, cancellationToken);
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
