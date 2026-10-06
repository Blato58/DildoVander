using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kufr.Backend.Features.Memory;

internal sealed record VectorHit(string FileId, double Score);
internal sealed class MemoryProviderException(string code, bool retryable = false, TimeSpan? retryAfter = null)
    : Exception(code)
{
    internal string Code => Message;
    internal bool Retryable => retryable;
    internal TimeSpan? RetryAfter => retryAfter;
}

// OpenAI .NET 2.14.0 lacks direct vector search and file attributes. Keep its
// version pinned; isolate the verified REST surface rather than invent SDK APIs.
// No local embeddings, automatic stores, hidden retries or idempotency headers.
internal sealed class OpenAiVectorMemoryProvider : IDisposable
{
    private readonly HttpClient http;
    private readonly string? key;
    private readonly HybridMemoryOptions options;
    private readonly bool ownsHttp;
    internal bool HasKey => !string.IsNullOrWhiteSpace(key);
    internal bool Configured => HasKey && options.PersonalStoreId.Length > 0 && options.MotifStoreId.Length > 0;
    public OpenAiVectorMemoryProvider(IConfiguration config, HybridMemoryOptions options)
        : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }), config["OPENAI_API_KEY"], options, true) { }
    internal OpenAiVectorMemoryProvider(HttpClient http, string? key, HybridMemoryOptions options, bool ownsHttp = false)
    { this.http = http; this.key = key; this.options = options; this.ownsHttp = ownsHttp; }

    private static string Id(string value)
    {
        if (value.Length is < 1 or > 128 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            throw new ArgumentException("Invalid OpenAI resource ID.");
        return value;
    }
    private async Task<JsonDocument> Request(HttpMethod method, string path, CancellationToken token,
        HttpContent? content = null, bool missingAllowed = false)
    {
        if (!HasKey) { content?.Dispose(); throw new MemoryProviderException("key_missing"); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        using var request = new HttpRequestMessage(method, new Uri("https://api.openai.com/v1/" + path)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.TryAddWithoutValidation("OpenAI-Beta", "assistants=v2");
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (missingAllowed && response.StatusCode == HttpStatusCode.NotFound) return JsonDocument.Parse("{\"deleted\":true}");
            if (!response.IsSuccessStatusCode)
                throw new MemoryProviderException("http_" + (int)response.StatusCode,
                    response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500,
                    response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null));
            if (response.Content.Headers.ContentLength > 1_048_576 || response.Content.Headers.ContentType?.MediaType != "application/json")
                throw new MemoryProviderException("invalid_response");
            using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var bytes = new byte[1_048_577]; var used = 0;
            try
            {
                while (used < bytes.Length)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(used), deadline.Token);
                    if (read == 0) break; used += read;
                }
                if (used > 1_048_576) throw new MemoryProviderException("response_too_large");
                var document = JsonDocument.Parse(Encoding.UTF8.GetString(bytes, 0, used));
                RejectDuplicateFields(document.RootElement);
                return document;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new MemoryProviderException("timeout", true); }
        catch (HttpRequestException) { throw new MemoryProviderException("network_unavailable", true); }
        catch (JsonException) { throw new MemoryProviderException("invalid_json"); }
    }
    private static void RejectDuplicateFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>();
            foreach (var field in element.EnumerateObject())
            { if (!names.Add(field.Name)) throw new MemoryProviderException("duplicate_field"); RejectDuplicateFields(field.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) RejectDuplicateFields(item);
    }
    private static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    internal async Task VerifyStoreAsync(string store, MemoryScope scope, CancellationToken token)
    {
        using var response = await Request(HttpMethod.Get, "vector_stores/" + Id(store), token);
        var root = response.RootElement;
        if (root.GetProperty("id").GetString() != store || root.GetProperty("status").GetString() == "expired")
            throw new MemoryProviderException("store_invalid");
        if (root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var (name, expected) in new[] { ("app_namespace", options.AppNamespace), ("environment", options.Environment), ("scope", scope.ToString()) })
                if (metadata.TryGetProperty(name, out var field) && field.GetString() != expected)
                    throw new MemoryProviderException("store_environment_mismatch");
        }
        // Explicitly configured pre-existing stores may have no application
        // metadata. File filters and local maps still isolate the installation.
    }
    internal async Task<string> CreateStoreAsync(MemoryScope scope, CancellationToken token)
    {
        using var response = await Request(HttpMethod.Post, "vector_stores", token, Json(new
        {
            name = options.AppNamespace + "-" + options.Environment + "-" + scope,
            metadata = new { app_namespace = options.AppNamespace, environment = options.Environment, scope = scope.ToString() }
        }));
        return Id(response.RootElement.GetProperty("id").GetString()!);
    }
    internal async Task<string> UploadAsync(string filename, string text, CancellationToken token)
    {
        if (filename.Length != 39 || !filename.StartsWith("km-", StringComparison.Ordinal) || !filename.EndsWith(".txt", StringComparison.Ordinal)
            || Encoding.UTF8.GetByteCount(text) > options.MaxRecordBytes) throw new ArgumentException("Invalid memory file.");
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("assistants"), "purpose");
        var bytes = new ByteArrayContent(Encoding.UTF8.GetBytes(text)); bytes.Headers.ContentType = new("text/plain");
        content.Add(bytes, "file", filename);
        using var response = await Request(HttpMethod.Post, "files", token, content);
        if (response.RootElement.GetProperty("filename").GetString() != filename) throw new MemoryProviderException("upload_filename_mismatch");
        return Id(response.RootElement.GetProperty("id").GetString()!);
    }
    internal async Task<string?> FindOwnedUploadAsync(string filename, CancellationToken token)
    {
        string? after = null;
        for (var page = 0; page < 100; page++)
        {
            using var response = await Request(HttpMethod.Get, "files?purpose=assistants&limit=100&order=desc"
                + (after is null ? "" : "&after=" + Id(after)), token);
            var matches = response.RootElement.GetProperty("data").EnumerateArray()
                .Where(f => f.GetProperty("filename").GetString() == filename).ToArray();
            if (matches.Length > 1) throw new MemoryProviderException("ambiguous_owned_upload");
            if (matches.Length == 1) return Id(matches[0].GetProperty("id").GetString()!);
            if (!response.RootElement.GetProperty("has_more").GetBoolean()) return null;
            var next = response.RootElement.GetProperty("last_id").GetString();
            if (string.IsNullOrEmpty(next) || next == after) throw new MemoryProviderException("invalid_file_page");
            after = next;
        }
        throw new MemoryProviderException("orphan_scan_limit", true);
    }
    internal async Task AttachAsync(string store, string file, IReadOnlyDictionary<string, object> attributes, CancellationToken token)
    {
        if (attributes.Count > 16 || attributes.Any(a => a.Key.Length is < 1 or > 64 || a.Value is null
            || a.Value is string s && s.Length > 512 || a.Value is not (string or bool or int or long or double)))
            throw new ArgumentException("Invalid vector attributes.");
        using var response = await Request(HttpMethod.Post, "vector_stores/" + Id(store) + "/files", token,
            Json(new { file_id = Id(file), attributes }));
        if (response.RootElement.GetProperty("id").GetString() != file) throw new MemoryProviderException("attachment_mismatch");
    }
    internal async Task<string> IndexStatusAsync(string store, string file, CancellationToken token)
    {
        using var response = await Request(HttpMethod.Get, "vector_stores/" + Id(store) + "/files/" + Id(file), token);
        var state = response.RootElement.GetProperty("status").GetString();
        return state is "in_progress" or "completed" or "failed" or "cancelled" ? state : throw new MemoryProviderException("invalid_index_status");
    }
    internal async Task<IReadOnlyList<VectorHit>> SearchAsync(string store, string query, object filters, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(query) || Encoding.UTF8.GetByteCount(query) > 2_000 || filters is null)
            throw new ArgumentException("Invalid memory search.");
        using var response = await Request(HttpMethod.Post, "vector_stores/" + Id(store) + "/search", token, Json(new
        {
            query, filters, max_num_results = options.SearchResults, rewrite_query = false,
            ranking_options = new { ranker = "none" }
        }));
        // Chunk content/attributes are deliberately not returned to the caller.
        // The authoritative local revision is resolved from file_id.
        return response.RootElement.GetProperty("data").EnumerateArray().Take(options.SearchResults)
            .Select(r => new VectorHit(Id(r.GetProperty("file_id").GetString()!), r.GetProperty("score").GetDouble())).ToArray();
    }
    internal async Task RemoveAssociationAsync(string store, string file, CancellationToken token)
    {
        using var response = await Request(HttpMethod.Delete, "vector_stores/" + Id(store) + "/files/" + Id(file), token, missingAllowed: true);
        if (!response.RootElement.GetProperty("deleted").GetBoolean()) throw new MemoryProviderException("deletion_unconfirmed", true);
    }
    internal async Task DeleteOwnedFileAsync(string file, string filename, CancellationToken token)
    {
        using (var metadata = await Request(HttpMethod.Get, "files/" + Id(file), token, missingAllowed: true))
        {
            if (metadata.RootElement.TryGetProperty("deleted", out _)) return;
            if (metadata.RootElement.GetProperty("filename").GetString() != filename || !filename.StartsWith("km-", StringComparison.Ordinal))
                throw new MemoryProviderException("file_ownership_mismatch");
        }
        using var response = await Request(HttpMethod.Delete, "files/" + Id(file), token, missingAllowed: true);
        if (!response.RootElement.GetProperty("deleted").GetBoolean()) throw new MemoryProviderException("file_deletion_unconfirmed", true);
    }
    public void Dispose() { if (ownsHttp) http.Dispose(); }
}
