using Kufr.Backend.Features.Conversations;

namespace Kufr.Backend.Features.Memory;

internal sealed record HybridMemoryOptions
{
    internal const long DefaultStoredBytes = 64L * 1024 * 1024;
    internal const long MaximumStoredBytes = 1024L * 1024 * 1024;
    public bool Enabled { get; init; } = true;
    public bool CloudIndexingEnabled { get; init; }
    public bool LongTermPersonalEnabled { get; init; } = true;
    public string AppNamespace { get; init; } = "kufr";
    public string Environment { get; init; } = "Development";
    public string PersonalStoreId { get; init; } = "";
    public string MotifStoreId { get; init; } = "";
    public int ContextTokens { get; init; } = 500;
    public int SearchResults { get; init; } = 5;
    public int TimeoutSeconds { get; init; } = 15;
    public int Concurrency { get; init; } = 2;
    public int MaxAttempts { get; init; } = 6;
    public int RetryBaseSeconds { get; init; } = 2;
    public int IndexPollSeconds { get; init; } = 2;
    public int IndexTimeoutSeconds { get; init; } = 120;
    // Accepted only for compatibility with existing deployment configuration.
    // User content has no automatic retention deadline.
    public int ConversationRetentionHours { get; init; } = 24;
    public int MotifRetentionDays { get; init; } = 1;
    public int PersonalRetentionDays { get; init; } = 30;
    public int MaxWritesPerDay { get; init; } = 2_000;
    public int MaxSearchesPerDay { get; init; } = 100;
    public int MaxUploadsPerDay { get; init; } = 500;
    public long MaxStoredBytes { get; init; } = DefaultStoredBytes;
    public int MaxRecordBytes { get; init; } = 2_000;
    public int MaxPendingJobs { get; init; } = 4_000;
    public string Namespace => AppNamespace + ":" + Environment;
    public string StoreFor(MemoryScope scope) => scope == MemoryScope.Personal ? PersonalStoreId : MotifStoreId;

    internal static HybridMemoryOptions Read(IConfiguration config, OperationJournal? journal = null)
    {
        var result = config.GetSection("Backend:Memory").Get<HybridMemoryOptions>() ?? new();
        result = result with
        {
            // Local persistence and explicitly permitted returning-guest recall
            // are application invariants. Old deployment flags must not revive
            // the dormant-memory architecture; cloud indexing remains opt-in.
            Enabled = true,
            LongTermPersonalEnabled = true,
            Environment = config["Backend:Memory:Environment"] ?? config["ASPNETCORE_ENVIRONMENT"] ?? config["DOTNET_ENVIRONMENT"] ?? "Production",
            PersonalStoreId = result.PersonalStoreId.Length > 0 ? result.PersonalStoreId : journal?.ReadSetting("memory_personal_store") ?? "",
            MotifStoreId = result.MotifStoreId.Length > 0 ? result.MotifStoreId : journal?.ReadSetting("memory_motif_store") ?? ""
        };
        static bool Label(string value) => value.Length is > 0 and <= 64
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
        static bool Store(string value) => value.Length == 0 || value.Length <= 128 && value.StartsWith("vs_", StringComparison.Ordinal)
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
        if (result.MaxStoredBytes is < 2_000 or > MaximumStoredBytes)
        {
            throw new InvalidOperationException("Backend:Memory:MaxStoredBytes must be between 2000 and 1073741824 UTF-8 content bytes.");
        }
        if (!Label(result.AppNamespace) || !Label(result.Environment) || !Store(result.PersonalStoreId) || !Store(result.MotifStoreId)
            || result.PersonalStoreId.Length > 0 && result.PersonalStoreId == result.MotifStoreId
            || result.CloudIndexingEnabled && !result.Enabled || result.LongTermPersonalEnabled && !result.Enabled
            || result.ContextTokens is < 300 or > 700 || result.SearchResults is < 1 or > 5
            || result.TimeoutSeconds is < 1 or > 60 || result.Concurrency is < 1 or > 4
            || result.MaxAttempts is < 1 or > 20 || result.RetryBaseSeconds is < 1 or > 60
            || result.IndexPollSeconds is < 1 or > 30 || result.IndexTimeoutSeconds is < 10 or > 600
            || result.MaxWritesPerDay is < 1 or > 2_000
            || result.MaxSearchesPerDay is < 1 or > 1_000 || result.MaxUploadsPerDay is < 1 or > 2_000
            || result.MaxRecordBytes is < 200 or > 2_000
            || result.MaxPendingJobs is < 1 or > 10_000)
            throw new InvalidOperationException("Invalid Backend:Memory configuration.");
        return result;
    }
}

internal enum MemoryScope { Conversation, EveningMotif, Personal }
internal enum MemoryKind { UserReportedFact, ConfirmedSelection, TopicUsed, FictionalJoke, LegacyUnverified, ModelInterpretation, VisualObservation, ProposedChoice, PendingQuestion }
internal sealed record GuestMemoryPermission(string DeviceId, string ConversationId, string GuestId,
    bool IdentityVerified, bool LongTermAllowed, bool CloudExportAllowed, long Version);
internal sealed record MemoryAccess(string DeviceId, string EveningId, string ConversationId,
    string PersonalityId, GuestMemoryPermission? Guest = null);
internal sealed record MemoryRecord(string MemoryId, MemoryScope Scope, string DeviceId, string EveningId,
    string ConversationId, string? GuestId, string? PersonalityId, MemoryKind Kind, string Text,
    string SourceReference, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, long Revision,
    bool IsActive, string? Supersedes, bool UseAllowed, bool ExportAllowed, long PermissionVersion, string AppNamespace = "");
internal sealed record MemoryWrite(string MemoryId, MemoryScope Scope, MemoryKind Kind, string Text,
    string SourceReference, long ExpectedRevision = 0, long? ExpectedEpoch = null, long? ExpectedDeletionEpoch = null,
    long? ExpectedFactEpoch = null);
internal sealed record MemoryIndexJob(long Id, string MemoryId, long Revision, string StoreId, string Action,
    int Attempts, string Filename, string? FileId, string State);
internal sealed record MemoryProviderStatus(string Provider, bool Configured, bool Degraded,
    string ConfigurationStatus, DateTimeOffset? LastVerifiedAt, int Pending, int Failed, int Deleting,
    long StoredBytes = 0, long CapacityBytes = 0, long RejectedWrites = 0,
    string StorageStatus = "available", bool StorageIncomplete = false,
    string? LastStorageErrorCode = null, DateTimeOffset? LastStorageErrorAt = null);

internal static class AnonymousMotifs
{
    // Closed vocabulary: never copy names, quotations or personal stories.
    internal static string FromTranscript(string text)
    {
        var topics = new[] { ("prác", "práce"), ("rozhod", "rozhodování"), ("ambic", "ambice"), ("nevím", "nerozhodnost"), ("chaos", "chaos") };
        var matches = topics.Where(t => text.Contains(t.Item1, StringComparison.OrdinalIgnoreCase)).Select(t => t.Item2).Take(2).ToArray();
        return matches.Length == 0 ? "" : "Použité abstraktní téma: " + string.Join(", ", matches) + ". Vyhni se opakování stejného motivu.";
    }
    internal static bool IsAllowed(string text) => new[] { "práce", "rozhodování", "ambice", "nerozhodnost", "chaos" }
        .SelectMany(a => new[] { a }.Concat(new[] { "práce", "rozhodování", "ambice", "nerozhodnost", "chaos" }.Select(b => a + ", " + b)))
        .Any(t => text == "Použité abstraktní téma: " + t + ". Vyhni se opakování stejného motivu.");
}
