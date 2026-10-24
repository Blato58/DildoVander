using System.Collections.Concurrent;
using System.ClientModel;
using System.Security.Cryptography;
using System.Text;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.Memory;
using Kufr.Backend.Features.Show;
using Kufr.Backend.Features.Wands;
using Microsoft.AspNetCore.DataProtection;

namespace Kufr.Backend.Features.TestingSessions;

// Composition boundary, not an alternate implementation of the product. Every
// persistent service below is the production service with a separate data root.
// Provider credentials/models remain the backend's configured values; no device
// credentials, public gallery target or vector-store IDs cross this boundary.
internal sealed class TestingResources : IDisposable
{
    private readonly ConcurrentDictionary<string, CharacterProfileStore> profiles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CharacterProfileStore> profileStoresByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly TestingSessionStore sessionStore;
    private readonly OpenAiWandGenerationProvider wandProvider;
    private readonly OpenAiVectorMemoryProvider vectorProvider;
    private readonly TestingDuelIdentities duelIdentities;
    private readonly TestingDuelSlot duelSlot = new();
    private readonly SemaphoreSlim generation = new(1, 1);
    private readonly IKeepsakeProvider? providerOverride;
    private readonly ICharacterEnrichmentProvider? characterOverride;
    internal IConfiguration Configuration { get; }
    internal string Root { get; }
    internal TestingSessionStore SessionsStore => sessionStore;
    internal OperationJournal Journal { get; }
    internal EveningMemoryStore MemoryStore { get; }
    internal HybridMemoryService HybridMemory { get; }
    internal WandGenerationStore Wands { get; }
    internal WandGenerationService WandService { get; }
    internal WandDecisionService Decisions { get; }
    internal KeepsakeStore Keepsakes { get; }
    internal DuelStore Duels { get; }
    internal DuelCommentaryService Commentary { get; }
    internal Func<string, string, DuelIdentitySnapshot?>? ResolveDuelIdentity
    {
        get => duelIdentities.Resolve;
        set => duelIdentities.Resolve = value;
    }
    internal Action<string, string, int, string>? ObserveDuelRound { get; set; }

    public TestingResources(IConfiguration configuration, TimeProvider clock)
        : this(configuration, clock, null, null, null, null) { }

    // This overload is for controlled offline integration tests only. Application
    // DI registers the two-argument constructor and cannot choose a fake provider.
    internal TestingResources(IConfiguration configuration, TimeProvider clock,
        IWandGenerationProvider? wandOverride, IKeepsakeProvider? keepsakeOverride,
        ICharacterEnrichmentProvider? enrichmentOverride, WandDecisionService? decisionsOverride)
    {
        providerOverride = keepsakeOverride;
        characterOverride = enrichmentOverride;
        Root = Path.GetFullPath((configuration["Backend:DatabasePath"]
            ?? throw new InvalidOperationException("Backend:DatabasePath missing.")) + ".testing");
        Directory.CreateDirectory(Root);
        sessionStore = new TestingSessionStore(Root);
        // Copy only service configuration needed by this graph. Physical device
        // secrets, production grants, twin targets and deployment controls are
        // never available to the virtual-device composition.
        var permittedSettings = configuration.AsEnumerable().Where(entry => entry.Key == "OPENAI_API_KEY"
            || entry.Key.StartsWith("Backend:GptLive:", StringComparison.OrdinalIgnoreCase)
            || entry.Key.StartsWith("Backend:Memory:", StringComparison.OrdinalIgnoreCase)
            || entry.Key.StartsWith("CustomerCharacters:", StringComparison.OrdinalIgnoreCase)
            || entry.Key.StartsWith("Wands:", StringComparison.OrdinalIgnoreCase)
            || entry.Key.StartsWith("Keepsake:", StringComparison.OrdinalIgnoreCase));
        Configuration = new ConfigurationBuilder().AddInMemoryCollection(permittedSettings).AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = Path.Combine(Root, "test-resources.sqlite"),
            ["Backend:Memory:CloudIndexingEnabled"] = "false",
            ["Backend:Memory:AppNamespace"] = "kufr-testing",
            ["Backend:Memory:Environment"] = "Testing",
            ["Backend:Memory:PersonalStoreId"] = "",
            ["Backend:Memory:MotifStoreId"] = "",
            ["Keepsake:DataRoot"] = Path.Combine(Root, "keepsakes"),
            ["Keepsake:DataProtectionRoot"] = Path.Combine(Root, "keys"),
            ["Keepsake:Enabled"] = "true",
            ["Keepsake:GenerationEnabled"] = "true",
            ["Keepsake:PortraitEnabled"] = "true",
            ["Keepsake:ProviderMode"] = "openai",
            ["Keepsake:PublicDeviceId"] = "",
            ["Show:DuelCommentaryEnabled"] = "true"
        }).Build();
        Journal = new(Configuration, clock);
        MemoryStore = new(Configuration, Journal, clock);
        var memoryOptions = HybridMemoryOptions.Read(Configuration, Journal);
        vectorProvider = new(Configuration, memoryOptions);
        HybridMemory = new(MemoryStore, vectorProvider, memoryOptions, clock);
        Wands = new(Configuration, clock);
        wandProvider = new(Configuration, Wands);
        WandService = new(Wands, wandOverride ?? wandProvider);
        Decisions = decisionsOverride ?? new(Configuration);
        var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Root, "keys")));
        Keepsakes = new(Configuration, clock, protection, MemoryStore);
        duelIdentities = new(MemoryStore);
        duelSlot.Observe = (device, id, round, quip) => ObserveDuelRound?.Invoke(device, id, round, quip);
        Duels = new(Configuration, clock, duelIdentities, duelSlot, protection);
        Commentary = new(Configuration);
        Duels.RecoverCommentaryClaims();
    }

    internal CharacterProfileStore Profiles(string owner, string characterId)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(characterId))
            throw new CharacterException("character_not_found", 404);
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(owner + ":" + characterId)));
        return profiles.GetOrAdd(key, id =>
        {
            var path = Path.Combine(Root, "characters", id);
            return profileStoresByPath.GetOrAdd(path, _ => new CharacterProfileStore(path));
        });
    }

    internal async Task<bool> ProcessNextCharacterEnrichmentAsync(CancellationToken token)
    {
        var charactersRoot = Path.Combine(Root, "characters");
        if (!Directory.Exists(charactersRoot)) return false;

        foreach (var databasePath in Directory.EnumerateFiles(charactersRoot, "customer-characters.sqlite", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(databasePath)!;
            var store = profileStoresByPath.GetOrAdd(directory, path => new CharacterProfileStore(path));
            var job = await store.ClaimAsync(token);
            if (job is null) continue;

            if (job.Attempt > 3)
            {
                await store.CompleteAsync(job, null, "character_job_interrupted", token);
                return true;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            using var provider = characterOverride is null ? new OpenAiCharacterEnrichmentProvider(Configuration, store) : null;
            try
            {
                if (!await store.CanDispatchAsync(job, timeout.Token)) return true;
                Task<CharacterEnrichment>? pending = null;
                var dispatched = sessionStore.TryStartUnlessFenced(job.AccountId,
                    () => pending = (characterOverride ?? provider!).EnrichAsync(job, timeout.Token));
                if (!dispatched)
                {
                    await store.FenceAccountDeletionAsync(job.AccountId, CancellationToken.None);
                    return true;
                }
                var enrichment = await pending!;
                timeout.Token.ThrowIfCancellationRequested();
                if (sessionStore.IsFenced(job.AccountId))
                {
                    await store.FenceAccountDeletionAsync(job.AccountId, CancellationToken.None);
                    return true;
                }
                await store.CompleteAsync(job, enrichment, null, timeout.Token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                var transient = error is HttpRequestException or OperationCanceledException
                    || error is ClientResultException response && (response.Status is 408 or 429 || response.Status >= 500);
                if (transient && job.Attempt < 3)
                {
                    await store.RetryAsync(job, token);
                }
                else
                {
                    var code = error is CharacterException character ? character.Code
                        : error is OperationCanceledException ? "character_provider_timeout"
                        : ProviderErrorCode(error, "character_provider_error");
                    await store.CompleteAsync(job, null, code, token);
                }
            }

            return true;
        }

        return false;
    }

    internal async Task GenerateKeepsakeAsync(string keepsakeId, CancellationToken token)
    {
        // Bound to the exact already-authorized archive by the owning service.
        // Never run a global worker from a user's request.
        await generation.WaitAsync(token);
        try
        {
            using var realProvider = providerOverride is null ? CreateKeepsakeProvider() : null;
            var provider = providerOverride ?? realProvider!;
            for (var count = 0; count < 3; count++)
            {
                token.ThrowIfCancellationRequested();
                var job = Keepsakes.LeaseNextForKeepsake(keepsakeId);
                if (job is null) return;
                try
                {
                    var request = Keepsakes.Dispatch(job);
                    if (request is null) throw new KeepsakeException("test_artifact_dispatch_revoked", 409);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromMinutes(4));
                    var output = await provider.GenerateAsync(request, timeout.Token);
                    timeout.Token.ThrowIfCancellationRequested();
                    // Offline test doubles deliberately have their own constructor;
                    // production composition must never persist synthetic output.
                    if (providerOverride is null && output.Synthetic) throw new KeepsakeException("test_artifact_synthetic_output_rejected", 502);
                    if (!Keepsakes.Complete(job, output)) throw new KeepsakeException("test_artifact_commit_revoked", 409);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    var code = error is KeepsakeException keepsake ? keepsake.Code
                        : error is OperationCanceledException ? "provider_timeout"
                        : ProviderErrorCode(error, "provider_attempt_unknown");
                    Keepsakes.MarkAmbiguous(job, code);
                    token.ThrowIfCancellationRequested();
                    throw new KeepsakeException(code, error is KeepsakeException known ? known.Status : 502);
                }
            }
        }
        finally { generation.Release(); }
    }

    private OpenAiKeepsakeProvider CreateKeepsakeProvider()
    {
        var key = Configuration["OPENAI_API_KEY"];
        if (string.IsNullOrWhiteSpace(key)) throw new KeepsakeException("test_artifact_provider_key_missing", 503);
        var story = Configuration["Keepsake:StoryModel"];
        var image = Configuration["Keepsake:ImageModel"];
        if (string.IsNullOrWhiteSpace(story) || string.IsNullOrWhiteSpace(image))
            throw new KeepsakeException("test_artifact_model_configuration_missing", 503);
        return new(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan }, key, story, image);
    }

    internal async Task<DuelView> GenerateDuelCommentaryAsync(string deviceId, string duelId, CancellationToken token)
    {
        var result = Duels.Read(duelId, deviceId);
        if (result.State != "Completed") throw new DuelException(409, "duel_not_completed");
        if (!Commentary.Available) throw new DuelException(503, "duel_commentary_provider_unavailable");
        var claimed = Duels.ClaimCommentary(duelId);
        if (claimed is null)
        {
            var current = Duels.Read(duelId, deviceId);
            return current.Commentary is not null ? current
                : throw new DuelException(409, "duel_commentary_not_dispatchable");
        }
        try
        {
            var attempt = await Commentary.GenerateAsync(claimed, token);
            Duels.CompleteCommentary(duelId, attempt);
            if (attempt.Commentary is null) throw new DuelException(502, "duel_commentary_provider_unavailable");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var code = error is OperationCanceledException ? "provider_timeout" : ProviderErrorCode(error, "provider_error_usage_unknown");
            Duels.CompleteCommentary(duelId, new(null, new(code, "not_confirmed")));
            token.ThrowIfCancellationRequested();
            throw new DuelException(502, code);
        }
        return Duels.Read(duelId, deviceId);
    }

    public void Dispose()
    {
        Commentary.Dispose(); Decisions.Dispose(); wandProvider.Dispose(); vectorProvider.Dispose(); Journal.Dispose();
        generation.Dispose();
    }

    // HTTP status is useful provider evidence without copying response bodies,
    // prompts, signed URLs or credentials into technical diagnostics.
    private static string ProviderErrorCode(Exception error, string fallback) =>
        error is ClientResultException { Status: >= 400 and <= 599 } provider
            ? "openai_http_" + provider.Status.ToString(System.Globalization.CultureInfo.InvariantCulture) : fallback;
}
