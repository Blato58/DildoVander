using System.Security.Cryptography;
using System.Text.Json;
using System.ClientModel.Primitives;

namespace Kufr.Backend.Features.OpenAi;

// Keep response limits ahead of SDK buffering. The injected client is also the
// test seam; production clients disable redirects. No hidden paid retries.
internal static class BoundedSdkTransport
{
    internal static HttpClient Create(HttpClient upstream, int maximumBytes, string[]? contentTypes = null) =>
        new(new Handler(upstream, maximumBytes, contentTypes));

    internal static HttpClientPipelineTransport Pipeline(HttpClient client) => new(client);

    private sealed class Handler(HttpClient upstream, int maximumBytes, string[]? contentTypes) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri is not { Scheme: "https", Host: "api.openai.com" })
                throw new InvalidDataException("Invalid provider endpoint.");
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Content = request.Content, Version = request.Version, VersionPolicy = request.VersionPolicy
            };
            foreach (var header in request.Headers) forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            HttpResponseMessage response;
            try { response = await upstream.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, token); }
            finally { forwarded.Content = null; } // SDK owns the request content.

            try
            {
                if (!response.IsSuccessStatusCode)
                    throw new InvalidDataException($"Provider HTTP {(int)response.StatusCode}.");
                if (response.Content.Headers.ContentLength > maximumBytes
                    || contentTypes is not null && !contentTypes.Contains(response.Content.Headers.ContentType?.MediaType))
                    throw new InvalidDataException("Unexpected provider response.");
                var buffer = new byte[maximumBytes + 1];
                try
                {
                    using var stream = await response.Content.ReadAsStreamAsync(token);
                    var used = 0;
                    while (used < buffer.Length)
                    {
                        var count = await stream.ReadAsync(buffer.AsMemory(used), token);
                        if (count == 0) break;
                        used += count;
                    }
                    if (used > maximumBytes) throw new InvalidDataException("Provider response exceeds its limit.");
                    if (contentTypes?.Contains("application/json") == true)
                    {
                        using var json = JsonDocument.Parse(buffer.AsMemory(0, used));
                        RejectDuplicateKeys(json.RootElement);
                    }
                    var bounded = new ByteArrayContent(buffer.AsSpan(0, used).ToArray());
                    foreach (var header in response.Content.Headers)
                        if (header.Key != "Content-Length") bounded.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    response.Content.Dispose();
                    response.Content = bounded;
                    return response;
                }
                finally { CryptographicOperations.ZeroMemory(buffer); }
            }
            catch { response.Dispose(); throw; }
        }
    }

    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in element.EnumerateObject())
            {
                if (!names.Add(field.Name)) throw new InvalidDataException("Duplicate provider field.");
                RejectDuplicateKeys(field.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateKeys(item);
    }
}
