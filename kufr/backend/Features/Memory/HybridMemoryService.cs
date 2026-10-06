using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;

namespace Kufr.Backend.Features.Memory;

internal sealed record MemoryRetrieval(string Status, IReadOnlyList<MemoryRecord> Records);
internal sealed record LiveMemoryContext(string Text, IReadOnlyList<string> References);

internal sealed class HybridMemoryService(EveningMemoryStore store, OpenAiVectorMemoryProvider provider,
    HybridMemoryOptions options, TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, MemoryRetrieval Result)> cache = new();
    private string configurationStatus = "not_verified";
    private DateTimeOffset? verifiedAt;
    private volatile bool degraded;
    private readonly SemaphoreSlim localWrites = new(1, 1);
    internal HybridMemoryOptions Options => options;
    internal long StoreEpoch => store.MemoryEpoch;
    internal async Task<MemoryRetrieval> ChangePersonalMemoryAsync(MemoryAccess access, LiveRequestIntent intent,
        DecisionEvidence source, CancellationToken token)
    {
        if (!options.Enabled || !options.LongTermPersonalEnabled || access.Guest is not { IdentityVerified: true, LongTermAllowed: true })
            return new("memory_not_permitted", []);
        await localWrites.WaitAsync(token);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                if (intent == LiveRequestIntent.Forget)
                {
                    var deleted = store.ForgetOwnPersonalMemory(access); ClearCache();
                    return new MemoryRetrieval(deleted ? "memory_deleted" : "memory_not_permitted", []);
                }
                if (intent != LiveRequestIntent.Remember) throw new ArgumentException("Unsupported memory change.");
                var text = Regex.Replace(source.Text, @"(?i)^\s*(zapamatuj si|ulož si|pamatuj si)\s*[:,]?\s*", "");
                if (string.IsNullOrWhiteSpace(text)) return new MemoryRetrieval("memory_information_missing", []);
                var bounded = LiveTextBudget.Bound(text, options.MaxRecordBytes);
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(access.ConversationId + ":personal:" + access.Guest.Version + ":" + source.Reference + ":" + source.Text));
                var id = new Guid(hash.AsSpan(0, 16)).ToString("D");
                var existing = store.CurrentMemory(id);
                if (existing is not null)
                    return new MemoryRetrieval(store.IsPermitted(existing, access) ? "memory_saved" : "memory_write_unavailable", []);
                var written = store.WriteMemory(access, new(id, MemoryScope.Personal, MemoryKind.UserReportedFact, bounded, source.Reference,
                    ExpectedEpoch: store.MemoryEpoch));
                ClearCache();
                return new MemoryRetrieval(written is null ? "memory_write_unavailable" : text == bounded ? "memory_saved" : "memory_saved_partial", []);
            }, token);
        }
        finally { localWrites.Release(); }
    }
    internal void InvalidateCurrentFacts(MemoryAccess access)
    {
        store.InvalidateConversationFacts(access); ClearCache();
    }
    internal async Task PersistContextAsync(MemoryAccess access, string generation,
        IReadOnlyList<LiveTranscriptSegment> segments, IReadOnlyList<DecisionEvidence> evidence, long epoch, CancellationToken token)
    {
        if (!options.Enabled) return;
        await localWrites.WaitAsync(token);
        try
        {
            await Task.Run(() =>
            {
                if (store.MemoryEpoch != epoch) return;
                store.AppendTimedTranscripts(access, generation, segments);
                foreach (var item in evidence)
                {
                    token.ThrowIfCancellationRequested();
                    if (store.MemoryEpoch != epoch) return;
                    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(access.ConversationId + ":" + item.Reference + ":" + item.Text));
                    var id = new Guid(hash.AsSpan(0, 16)).ToString("D");
                    if (store.CurrentMemory(id) is not null) continue;
                    var text = LiveTextBudget.Bound(item.Text, options.MaxRecordBytes);
                    if (text.Length == 0) continue;
                    var written = store.WriteMemory(access, new(id, MemoryScope.Conversation, MemoryKind.UserReportedFact,
                        text, item.Reference, ExpectedEpoch: epoch));
                    if (written is null) return;
                    epoch = store.MemoryEpoch;
                }
            }, token);
        }
        finally { localWrites.Release(); }
    }
    internal MemoryProviderStatus Status
    {
        get
        {
            var b = store.MemoryBacklog();
            return new("openai_vector_store", provider.Configured, degraded,
                !options.Enabled ? "disabled" : !provider.HasKey ? "key_missing" : !provider.Configured ? "store_ids_missing" : configurationStatus,
                verifiedAt, b.Pending, b.Failed, b.Deleting);
        }
    }
    internal async Task VerifyAsync(CancellationToken token)
    {
        if (!provider.Configured) { degraded = true; throw new MemoryProviderException("configuration_missing"); }
        try
        {
            await provider.VerifyStoreAsync(options.PersonalStoreId, MemoryScope.Personal, token);
            await provider.VerifyStoreAsync(options.MotifStoreId, MemoryScope.EveningMotif, token);
            configurationStatus = "verified"; verifiedAt = clock.GetUtcNow(); degraded = false;
        }
        catch (MemoryProviderException e) { configurationStatus = e.Code; degraded = true; throw; }
        catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or ArgumentException)
        { configurationStatus = "invalid_provider_response"; degraded = true; throw new MemoryProviderException(configurationStatus); }
    }
    internal void ProviderFailed() => degraded = true;
    internal void ProviderRecovered() => degraded = false;

    internal static object Filters(HybridMemoryOptions options, MemoryAccess access, MemoryScope scope, DateTimeOffset now,
        bool conversationOnly = false)
    {
        var filters = new List<object>();
        void Add(string key, object value, string type = "eq") => filters.Add(new { type, key, value });
        Add("app_namespace", options.Namespace); Add("device_id", access.DeviceId); Add("scope", scope.ToString());
        Add("active", true); Add("expires_at", now.ToUnixTimeSeconds(), "gt");
        if (scope == MemoryScope.Personal)
        {
            if (access.Guest is not { IdentityVerified: true, LongTermAllowed: true, CloudExportAllowed: true })
                throw new InvalidOperationException("Personal memory search requires a verified permitted guest.");
            Add("guest_id", access.Guest.GuestId);
            if (conversationOnly) Add("conversation_id", access.ConversationId);
        }
        else if (scope == MemoryScope.EveningMotif)
        {
            Add("evening_id", access.EveningId); Add("personality_id", access.PersonalityId);
        }
        else throw new InvalidOperationException("Conversation memory remains local.");
        return new { type = "and", filters };
    }

    internal async Task<MemoryRetrieval> RetrieveAsync(MemoryAccess access, MemoryScope scope, string query,
        CancellationToken token, bool conversationOnly = false)
    {
        if (!options.Enabled) return new("memory_unavailable", []);
        if (scope == MemoryScope.Personal && (!options.LongTermPersonalEnabled || access.Guest is not { IdentityVerified: true, LongTermAllowed: true }))
            return new("memory_not_found", []);
        var local = store.LocalMemory(access, scope).Where(r => !conversationOnly || r.ConversationId == access.ConversationId).Take(options.SearchResults).ToArray();
        if (!options.CloudIndexingEnabled || !provider.Configured
            || scope == MemoryScope.Personal && access.Guest is not { CloudExportAllowed: true })
            return new(local.Length > 0 ? "local_context" : "memory_unavailable", local);
        var epoch = store.MemoryEpoch;
        var cacheKey = JsonSerializer.Serialize(new { options.Namespace, access, scope, query, epoch, conversationOnly });
        if (cache.TryGetValue(cacheKey, out var saved) && saved.Expires > clock.GetUtcNow())
            return saved.Result with { Records = saved.Result.Records.Where(r => store.CurrentMemory(r.MemoryId) is { } current
                && current.Revision == r.Revision && store.IsPermitted(current, access, cloud: true)).ToArray() };
        if (!store.SpendMemoryQuota("search")) return new("memory_unavailable", local);
        try
        {
            var hits = await provider.SearchAsync(options.StoreFor(scope), LiveTextBudget.Bound(query, 1900),
                Filters(options, access, scope, clock.GetUtcNow(), conversationOnly), token);
            var resolved = hits.Where(h => double.IsFinite(h.Score) && h.Score >= 0)
                .Select(h => store.ResolveFile(options.StoreFor(scope), h.FileId, access, scope)).OfType<MemoryRecord>()
                .Where(r => !conversationOnly || r.ConversationId == access.ConversationId)
                .DistinctBy(r => (r.MemoryId, r.Revision)).Take(options.SearchResults).ToArray();
            // Every hit, including its text, is rehydrated from SQLite. A foreign
            // hit/unknown FileId contributes no data even if the provider erred.
            var result = new MemoryRetrieval(resolved.Length > 0 ? "memory_found" : "memory_not_found", resolved);
            if (cache.Count >= 64) cache.Clear();
            if (epoch == store.MemoryEpoch) cache[cacheKey] = (clock.GetUtcNow().AddSeconds(10), result);
            degraded = false; return result;
        }
        catch (Exception e) when (e is MemoryProviderException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { degraded = true; return new("memory_unavailable", local); }
    }
    internal void ClearCache() => cache.Clear();
    internal string BuildContext(MemoryAccess access, IEnumerable<MemoryRecord>? retrieved = null, ISet<string>? delivered = null,
        int? tokenBudget = null)
    {
        var limit = Math.Min(options.ContextTokens, tokenBudget ?? options.ContextTokens);
        var rows = store.LocalMemory(access, MemoryScope.Conversation)
            .Concat(retrieved ?? store.LocalMemory(access)).DistinctBy(r => (r.MemoryId, r.Revision))
            .Where(r => store.CurrentMemory(r.MemoryId) is { } current && current.Revision == r.Revision && store.IsPermitted(current, access));
        var selected = new List<string>(); var used = 32;
        foreach (var row in rows)
        {
            var reference = row.MemoryId + ":" + row.Revision;
            if (delivered?.Contains(reference) == true) continue;
            var label = row.Kind switch
            {
                MemoryKind.TopicUsed => "Anonymní motiv proti opakování",
                MemoryKind.UserReportedFact => row.Scope == MemoryScope.Conversation
                    ? "Tvrzení aktuálního hosta (neověřená skutečnost)" : "Historické tvrzení povoleného hosta (neověřená skutečnost)",
                MemoryKind.FictionalJoke => "Fiktivní vtip, není osobní fakt",
                _ => "Dřívější potvrzený aplikační výsledek"
            };
            // JSON quoting prevents data from forging the framing delimiter.
            if (limit - used <= 130) break;
            var text = label + ": " + JsonSerializer.Serialize(LiveTextBudget.Bound(row.Text, limit - used - 100), Realtime.RealtimeProtocol.JsonOptions) + ".";
            var size = LiveTextBudget.UpperBound(text);
            if (used + size > limit) continue;
            selected.Add(text); used += size; delivered?.Add(reference);
        }
        return string.Join("\n", selected);
    }
    internal async Task<LiveMemoryContext> StartupContextAsync(MemoryAccess access, CancellationToken token)
    {
        if (!options.Enabled) return new("", []);
        var records = new List<MemoryRecord>();
        // Lookup happens only at a meaningful boundary, never before every reply.
        if (access.Guest is { IdentityVerified: true, LongTermAllowed: true })
            records.AddRange((await RetrieveAsync(access, MemoryScope.Personal, "Předchozí potvrzený výběr a dobrovolně uložené preference hosta.", token)).Records);
        records.AddRange((await RetrieveAsync(access, MemoryScope.EveningMotif, "Nedávno použité abstraktní motivy vtipů a témata, kterým se vyhnout.", token)).Records);
        var delivered = new HashSet<string>(StringComparer.Ordinal);
        return new(BuildContext(access, records, delivered), delivered.ToArray());
    }
}

internal sealed class MemoryIndexWorker(EveningMemoryStore store, OpenAiVectorMemoryProvider provider,
    HybridMemoryService service, HybridMemoryOptions options, TimeProvider clock, ILogger<MemoryIndexWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!provider.Configured) return; // deletion/retention must also drain after disabling new memory writes
        await Task.WhenAll(Enumerable.Range(0, options.Concurrency).Select(_ => Loop(stoppingToken)));
    }
    private async Task Loop(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.IndexPollSeconds), clock);
        while (!token.IsCancellationRequested)
        {
            var job = store.ClaimMemoryJob();
            if (job is null) { if (!await timer.WaitForNextTickAsync(token)) return; continue; }
            try
            {
                await ProcessAsync(job, token); store.FinishMemoryJob(job); service.ProviderRecovered();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; } // lease is recovered after restart
            catch (Exception e) when (e is MemoryProviderException or InvalidDataException or HttpRequestException or OperationCanceledException
                or InvalidOperationException or KeyNotFoundException or ArgumentException)
            {
                var error = e as MemoryProviderException;
                var retryable = error?.Retryable ?? e is HttpRequestException or OperationCanceledException;
                var backoff = TimeSpan.FromSeconds(Math.Min(300, options.RetryBaseSeconds * Math.Pow(2, Math.Min(job.Attempts - 1, 8)))
                    + Random.Shared.NextDouble());
                if (error?.RetryAfter is { } rateLimit && rateLimit > backoff) backoff = rateLimit;
                var retry = retryable && job.Attempts < options.MaxAttempts ? backoff : (TimeSpan?)null;
                store.FinishMemoryJob(job, error?.Code ?? (e is OperationCanceledException ? "index_timeout" : "invalid_provider_response"), retry);
                if (retry is null && job.Action == "index") store.QueueMemoryDeletion(job);
                service.ProviderFailed();
                logger.LogWarning("Memory job {JobId} failed with {Code}", job.Id, error?.Code ?? "invalid_provider_response");
            }
        }
    }
    internal async Task ProcessAsync(MemoryIndexJob job, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.IndexTimeoutSeconds)); token = deadline.Token;
        var record = store.CurrentMemory(job.MemoryId);
        if (job.Action == "delete")
        {
            if (store.IndexUploadInProgress(job)) throw new MemoryProviderException("upload_still_running", true, TimeSpan.FromSeconds(2));
            var file = job.FileId ?? await provider.FindOwnedUploadAsync(job.Filename, token);
            if (file is null && job.State == "uploading")
                throw new MemoryProviderException("upload_outcome_unverified", true);
            if (file is not null)
            {
                store.SetMemoryIndex(job, "deleting", file);
                if (!store.OwnsUnsharedFile(job, file)) throw new MemoryProviderException("file_not_owned_or_shared");
                store.MarkDeletionRequested(job);
                await provider.RemoveAssociationAsync(job.StoreId, file, token);
                await provider.DeleteOwnedFileAsync(file, job.Filename, token);
            }
            store.SetMemoryIndex(job, "deleted"); return;
        }
        bool Current()
        {
            var current = store.CurrentMemory(job.MemoryId);
            return options.Enabled && options.CloudIndexingEnabled && current is { IsActive: true, ExportAllowed: true }
                && current.Revision == job.Revision && current.Scope != MemoryScope.Conversation
                && job.StoreId == options.StoreFor(current.Scope) && store.IsPermitted(current,
                    new(current.DeviceId, current.EveningId, current.ConversationId, current.PersonalityId ?? "",
                        store.GuestPermission(current.DeviceId, current.ConversationId)), cloud: true);
        }
        if (!Current()) { store.SetMemoryIndex(job, job.State == "uploading" ? "uploading" : "obsolete"); store.QueueMemoryDeletion(job); return; }
        if (string.IsNullOrWhiteSpace(job.StoreId)) throw new MemoryProviderException("store_id_missing");
        await provider.VerifyStoreAsync(job.StoreId, record!.Scope, token);
        var fileId = job.FileId;
        if (fileId is null && job.State == "uploading")
        {
            // A timed-out POST is ambiguous. Resolve its exact opaque filename
            // before another upload; never assume an undocumented idempotency key.
            fileId = await provider.FindOwnedUploadAsync(job.Filename, token);
            if (fileId is null) throw new MemoryProviderException("upload_outcome_unverified", true);
        }
        if (!Current()) { store.SetMemoryIndex(job, "obsolete", fileId); store.QueueMemoryDeletion(job); return; }
        if (fileId is null)
        {
            if (!store.SpendMemoryQuota("upload")) throw new MemoryProviderException("upload_quota", true, TimeSpan.FromHours(1));
            store.SetMemoryIndex(job, "uploading");
            fileId = await provider.UploadAsync(job.Filename, record.Text, token);
        }
        store.SetMemoryIndex(job, "uploaded", fileId);
        if (!Current()) { store.SetMemoryIndex(job, "completed", fileId); return; } // schedules delete for invalidated record
        var attributes = new Dictionary<string, object>
        {
            ["app_namespace"] = options.Namespace, ["scope"] = record.Scope.ToString(), ["device_id"] = record.DeviceId,
            ["evening_id"] = record.EveningId, ["conversation_id"] = record.ConversationId, ["memory_id"] = record.MemoryId,
            ["revision"] = record.Revision, ["kind"] = record.Kind.ToString(), ["active"] = true,
            ["expires_at"] = record.ExpiresAt.ToUnixTimeSeconds()
        };
        if (record.GuestId is not null) attributes["guest_id"] = record.GuestId;
        if (record.PersonalityId is not null) attributes["personality_id"] = record.PersonalityId;
        await provider.AttachAsync(job.StoreId, fileId, attributes, token);
        using var polling = new PeriodicTimer(TimeSpan.FromSeconds(options.IndexPollSeconds), clock);
        while (true)
        {
            var state = await provider.IndexStatusAsync(job.StoreId, fileId, token);
            if (state == "completed") break;
            if (state is "failed" or "cancelled") throw new MemoryProviderException("index_" + state);
            if (!Current()) { store.SetMemoryIndex(job, "completed", fileId); return; }
            await polling.WaitForNextTickAsync(token);
        }
        // Exact revision/permission is checked after cloud completion as well.
        store.SetMemoryIndex(job, Current() ? "completed" : "obsolete", fileId);
    }
}
