using System.Text;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Memory;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

internal static class MemoryCapacityTests
{
    private const long MinimumCapacity = 2_000;
    private const long DefaultCapacity = 64L * 1024 * 1024;
    private const long MaximumCapacity = 1024L * 1024 * 1024;

    internal static Task RunAsync()
    {
        VerifyConfigurationBounds();
        VerifyUtf8BoundariesAndGlobalAccounting();
        VerifyRevisionsRemainCounted();
        VerifyDuplicateAndPartialTranscriptWrites();
        VerifyConcurrentWritesStayWithinCapacity();
        VerifyReopenAndLoweredCapacity();
        VerifyProviderStorageStatus();
        MeasureRepresentativeArchive();
        return Task.CompletedTask;
    }

    private static void VerifyConfigurationBounds()
    {
        Check(HybridMemoryOptions.Read(new ConfigurationBuilder().Build()).MaxStoredBytes == DefaultCapacity,
            "absent capacity setting uses the 64 MiB default");
        Check(ReadOptions(MinimumCapacity).MaxStoredBytes == MinimumCapacity, "minimum capacity is accepted");
        Check(ReadOptions(DefaultCapacity).MaxStoredBytes == DefaultCapacity, "64 MiB default capacity");
        Check(ReadOptions(MaximumCapacity).MaxStoredBytes == MaximumCapacity, "1 GiB maximum capacity is accepted");

        foreach (var invalid in new[] { "0", "-1", "1073741825", "NaN" })
        {
            try
            {
                ReadOptions(invalid);
            }
            catch (Exception exception) when (exception is InvalidOperationException or FormatException)
            {
                Check(exception.Message.Contains("Backend:Memory:MaxStoredBytes", StringComparison.Ordinal),
                    $"invalid capacity {invalid} identifies its configuration key");
                continue;
            }

            throw new Exception($"Memory capacity: invalid configured value {invalid} was accepted.");
        }
    }

    private static void VerifyUtf8BoundariesAndGlobalAccounting()
    {
        using var fixture = new Fixture(MinimumCapacity);
        const string halfCapacity = "é";
        var transcript = string.Concat(Enumerable.Repeat(halfCapacity, 500));
        Check(Encoding.UTF8.GetByteCount(transcript) == 1_000, "Unicode fixture uses actual UTF-8 bytes");
        Check(fixture.Store.AppendTimedTranscripts(fixture.Access, "capacity-unicode",
            [new("transcript", "user", transcript, 0, 1, "event-transcript")]), "transcript below global capacity is committed");

        var fact = fixture.Store.WriteMemory(fixture.Access,
            Write(Guid.NewGuid().ToString("D"), string.Concat(Enumerable.Repeat("ž", 500))));
        Check(fact is not null && fixture.Store.MemoryStorageStatus().StoredBytes == MinimumCapacity,
            "transcript and record bytes are counted together exactly at the boundary");
        Check(fixture.Store.WriteMemory(fixture.Access, Write(Guid.NewGuid().ToString("D"), "x")) is null,
            "a write above the aggregate byte limit is rejected");
        Check(fixture.Store.CurrentMemory(fact!.MemoryId) is { IsActive: true }
            && fixture.Store.MemoryStorageStatus().StoredBytes == MinimumCapacity,
            "rejection preserves prior data and does not exceed capacity");
    }

    private static void VerifyRevisionsRemainCounted()
    {
        using var fixture = new Fixture(MinimumCapacity);
        var memoryId = Guid.NewGuid().ToString("D");
        var original = fixture.Store.WriteMemory(fixture.Access, Write(memoryId, new string('a', 1_000)))!;
        var correction = fixture.Store.WriteMemory(fixture.Access,
            new(memoryId, MemoryScope.Conversation, MemoryKind.UserReportedFact, new string('b', 1_000),
                "revision-correction", ExpectedRevision: original.Revision))!;

        Check(correction.Revision == 2 && fixture.Store.MemoryStorageStatus().StoredBytes == MinimumCapacity,
            "both old and current revision content consume global capacity");
        Check(fixture.Store.WriteMemory(fixture.Access, Write(Guid.NewGuid().ToString("D"), "x")) is null,
            "historical revisions cannot be overwritten to make room");
        Check(fixture.Store.CurrentMemory(memoryId) is { Revision: 2, Text: var text } && text == new string('b', 1_000),
            "a capacity rejection leaves the latest revision intact");
    }

    private static void VerifyDuplicateAndPartialTranscriptWrites()
    {
        using var fixture = new Fixture(MinimumCapacity);
        var oneHundredBytes = new string('t', 100);
        var initial = new LiveTranscriptSegment("same", "user", new string('i', 1_800), 0, 1, "event-same");
        Check(fixture.Store.AppendTimedTranscripts(fixture.Access, "capacity-duplicates", [initial]),
            "first transcript segment commits");
        var beforeDuplicate = fixture.Store.MemoryStorageStatus();
        Check(fixture.Store.AppendTimedTranscripts(fixture.Access, "capacity-duplicates", [initial]),
            "duplicate transcript snapshot is idempotent");
        Check(fixture.Store.MemoryStorageStatus().StoredBytes == beforeDuplicate.StoredBytes,
            "duplicate transcript snapshot consumes no capacity");

        var partial = new[]
        {
            new LiveTranscriptSegment("accepted-1", "user", oneHundredBytes, 2, 3, "event-1"),
            new LiveTranscriptSegment("accepted-2", "assistant", oneHundredBytes, 4, 5, "event-2"),
            new LiveTranscriptSegment("rejected", "user", oneHundredBytes, 6, 7, "event-3")
        };
        Check(!fixture.Store.AppendTimedTranscripts(fixture.Access, "capacity-partial", partial),
            "partial transcript batch reports at least one capacity rejection");
        var status = fixture.Store.MemoryStorageStatus();
        Check(status.StoredBytes == MinimumCapacity && status.RejectedWrites == 1,
            "accepted transcript segments commit while the rejected segment increments the durable counter");
        using var database = fixture.OpenArchive();
        using var command = database.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM live_transcripts WHERE session_generation IN ('capacity-duplicates','capacity-partial')";
        Check(Convert.ToInt32(command.ExecuteScalar()) == 3, "duplicate and partial transcript outcomes are durable");
    }

    private static void VerifyConcurrentWritesStayWithinCapacity()
    {
        using var fixture = new Fixture(MinimumCapacity);
        var results = Enumerable.Range(0, 64).Select(index => Task.Run(() =>
            fixture.Store.WriteMemory(fixture.Access, Write(Guid.NewGuid().ToString("D"), new string((char)('a' + index % 26), 100)))
                is not null)).ToArray();
        Task.WaitAll(results);

        var status = fixture.Store.MemoryStorageStatus();
        Check(status.StoredBytes <= MinimumCapacity && results.Count(result => result.Result) == MinimumCapacity / 100,
            "concurrent memory writes serialize capacity checks and never exceed the configured limit");
        Check(status.RejectedWrites > 0, "concurrent over-capacity writes are durably counted as rejected");
    }

    private static void VerifyReopenAndLoweredCapacity()
    {
        using var fixture = new Fixture(4_000);
        var first = fixture.Store.WriteMemory(fixture.Access,
            Write(Guid.NewGuid().ToString("D"), new string('a', 1_800)))
            ?? throw new Exception("Memory capacity: first pre-reopen record was rejected.");
        var second = fixture.Store.WriteMemory(fixture.Access,
            Write(Guid.NewGuid().ToString("D"), new string('b', 1_000)))
            ?? throw new Exception("Memory capacity: second pre-reopen record was rejected.");
        Check(fixture.Store.WriteMemory(fixture.Access, Write(Guid.NewGuid().ToString("D"), new string('r', 1_201))) is null,
            "capacity rejection counter is populated before reopening");

        var reopened = new EveningMemoryStore(fixture.Configuration(2_500), fixture.Journal, fixture.Clock);
        var reopenedStatus = reopened.MemoryStorageStatus();
        Check(reopenedStatus.StoredBytes == 2_800 && reopenedStatus.RejectedWrites == 1
            && reopened.CurrentMemory(first.MemoryId) is not null && reopened.CurrentMemory(second.MemoryId) is not null,
            "reopening the same database preserves records and capacity counters");
        Check(reopened.WriteMemory(fixture.Access, Write(Guid.NewGuid().ToString("D"), "x")) is null,
            "lowering capacity below existing use rejects additional data without deleting records");
        Check(reopened.CurrentMemory(first.MemoryId) is not null && reopened.MemoryStorageStatus().StoredBytes == 2_800,
            "lowered capacity leaves over-limit historical data intact");

        var historicalErrorAt = reopened.MemoryStorageStatus().LastErrorAt;
        var recoveredConfiguration = fixture.Configuration(4_000);
        var recoveredStore = new EveningMemoryStore(recoveredConfiguration, fixture.Journal, fixture.Clock);
        Check(recoveredStore.WriteMemory(fixture.Access, Write(Guid.NewGuid().ToString("D"), new string('n', 100))) is not null
            && recoveredStore.MemoryStorageStatus().StoredBytes == 2_900,
            "reopening with increased capacity admits new content alongside the preserved archive");
        using var provider = new OpenAiVectorMemoryProvider(new HttpClient(new OfflineHandler()), null,
            HybridMemoryOptions.Read(recoveredConfiguration));
        var recoveredService = new HybridMemoryService(recoveredStore, provider,
            HybridMemoryOptions.Read(recoveredConfiguration), fixture.Clock);
        Check(recoveredService.Status.StorageStatus == "available"
            && recoveredService.Status.StorageIncomplete
            && recoveredService.Status.LastStorageErrorCode == "archive_capacity"
            && recoveredService.Status.LastStorageErrorAt == historicalErrorAt
            && historicalErrorAt is not null,
            "raising capacity restores available status while historical rejection diagnostics persist");
        Check(recoveredStore.DeleteMemory(fixture.Access.DeviceId, first.MemoryId, first.Revision)
            && recoveredStore.MemoryStorageStatus() is { StoredBytes: 1_100, RejectedWrites: 2, LastErrorCode: "archive_capacity" },
            "manual memory deletion releases stored text bytes while keeping rejection history");

        var justAboveLegacyLimit = new Fixture(2_102_000);
        using (justAboveLegacyLimit)
        {
            var legacyBoundaryBatch = Enumerable.Range(0, 1_051).Select(index =>
                new LiveTranscriptSegment($"legacy-{index}", "user", new string('c', 2_000),
                    index * 1_000, index * 1_000 + 500, $"legacy-event-{index}")).ToArray();
            Check(justAboveLegacyLimit.Store.AppendTimedTranscripts(justAboveLegacyLimit.Access,
                "above-legacy-capacity", legacyBoundaryBatch)
                && justAboveLegacyLimit.Store.MemoryStorageStatus().StoredBytes == 2_102_000,
                "capacity above the previous two-million-byte ceiling is accepted");
        }
    }

    private static void VerifyProviderStorageStatus()
    {
        using var fixture = new Fixture(MinimumCapacity);
        using var provider = new OpenAiVectorMemoryProvider(new HttpClient(new OfflineHandler()), null,
            HybridMemoryOptions.Read(fixture.Configuration(MinimumCapacity)));
        var service = new HybridMemoryService(fixture.Store, provider,
            HybridMemoryOptions.Read(fixture.Configuration(MinimumCapacity)), fixture.Clock);

        Check(service.Status.StorageStatus == "available" && !service.Status.StorageIncomplete
            && service.Status.LastStorageErrorCode is null && service.Status.LastStorageErrorAt is null,
            "new archive reports available storage and no previous capacity error");
        Check(fixture.Store.WriteMemory(fixture.Access,
            Write(Guid.NewGuid().ToString("D"), new string('w', 1_600))) is not null
            && service.Status.StorageStatus == "warning", "storage warning begins at eighty percent");
        Check(fixture.Store.WriteMemory(fixture.Access,
            Write(Guid.NewGuid().ToString("D"), new string('f', 400))) is not null
            && service.Status.StorageStatus == "full", "storage reports full at the configured limit");
        Check(fixture.Store.WriteMemory(fixture.Access, Write(Guid.NewGuid().ToString("D"), "x")) is null
            && service.Status.StorageIncomplete
            && service.Status.LastStorageErrorCode == "archive_capacity"
            && service.Status.LastStorageErrorAt is { Offset: var offset } && offset == TimeSpan.Zero,
            "historical rejections expose an archive-capacity code and a UTC timestamp");
    }

    private static void MeasureRepresentativeArchive()
    {
        using var fixture = new Fixture(DefaultCapacity);
        long transcriptBytes = 0;
        long factBytes = 0;
        var phrase = "Večer jsme mluvili o plánech a cestě domů; host popsal, co ho potěšilo a co chce změnit. ";
        var segmentText = string.Concat(Enumerable.Repeat(phrase, 2))[..120];

        for (var conversationIndex = 0; conversationIndex < 100; conversationIndex++)
        {
            var access = fixture.Access with { ConversationId = Guid.NewGuid().ToString("D") };
            for (var factIndex = 0; factIndex < 5; factIndex++)
            {
                var text = $"Host {conversationIndex} zmínil plán číslo {factIndex}: český text pro kapacitní měření.";
                factBytes += Encoding.UTF8.GetByteCount(text);
                Check(fixture.Store.WriteMemory(access,
                    Write(Guid.NewGuid().ToString("D"), text, $"fixture:{conversationIndex}:{factIndex}")) is not null,
                    "representative fixture fact is accepted");
            }

            var segments = Enumerable.Range(0, 100).Select(segmentIndex =>
                new LiveTranscriptSegment($"segment-{segmentIndex}", "user", segmentText,
                    segmentIndex * 1_000, segmentIndex * 1_000 + 500, $"event-{segmentIndex}")).ToArray();
            transcriptBytes += Encoding.UTF8.GetByteCount(segmentText) * segments.Length;
            Check(fixture.Store.AppendTimedTranscripts(access, "representative-fixture", segments),
                "representative fixture transcript batch is accepted");
        }

        var measuredTextBytes = transcriptBytes + factBytes;
        var status = fixture.Store.MemoryStorageStatus();
        var sqlitePath = fixture.DatabasePath + ".evening.sqlite";
        var sqliteBytes = new FileInfo(sqlitePath).Length;
        Check(status.StoredBytes == measuredTextBytes && measuredTextBytes > 0 && sqliteBytes > measuredTextBytes,
            "representative fixture reports actual text and SQLite archive sizes");
        Console.WriteLine($"Memory capacity fixture: 100 conversations, 10,000 transcript segments, 500 facts; UTF-8 text {measuredTextBytes:N0} bytes (transcripts {transcriptBytes:N0}, facts {factBytes:N0}); SQLite file {sqliteBytes:N0} bytes.");
    }

    private static MemoryWrite Write(string id, string text, string source = "capacity-test") =>
        new(id, MemoryScope.Conversation, MemoryKind.UserReportedFact, text, source);

    private static HybridMemoryOptions ReadOptions(long capacity) => ReadOptions(capacity.ToString());

    private static HybridMemoryOptions ReadOptions(string capacity) => HybridMemoryOptions.Read(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:Memory:MaxStoredBytes"] = capacity
        }).Build());

    private static void Check(bool condition, string detail)
    {
        if (!condition)
        {
            throw new Exception("Memory capacity: " + detail);
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "kufr-memory-capacity-" + Guid.NewGuid().ToString("N"));
        internal string DatabasePath => Path.Combine(DirectoryPath, "journal.sqlite");
        internal AdjustableTimeProvider Clock { get; } = new(DateTimeOffset.Parse("2026-10-05T10:00:00Z"));
        internal OperationJournal Journal { get; }
        internal EveningMemoryStore Store { get; }
        internal MemoryAccess Access { get; }

        internal Fixture(long capacity)
        {
            Directory.CreateDirectory(DirectoryPath);
            var configuration = Configuration(capacity);
            Journal = new OperationJournal(configuration, Clock);
            Store = new EveningMemoryStore(configuration, Journal, Clock);
            Access = new MemoryAccess("capacity-device", Store.GetStatus("capacity-device").EveningId,
                Guid.NewGuid().ToString("D"), PersonalityCatalog.OllivanderId);
        }

        internal IConfiguration Configuration(long capacity) => new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Backend:DatabasePath"] = DatabasePath,
                ["Backend:Memory:Enabled"] = "true",
                ["Backend:Memory:AppNamespace"] = "capacity-tests",
                ["Backend:Memory:Environment"] = "Development",
                ["Backend:Memory:MaxStoredBytes"] = capacity.ToString()
            }).Build();

        internal SqliteConnection OpenArchive()
        {
            var connection = new SqliteConnection($"Data Source={DatabasePath}.evening.sqlite;Pooling=False");
            connection.Open();
            return connection;
        }

        public void Dispose()
        {
            Journal.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(DirectoryPath, true);
        }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Capacity status checks must not call the provider.");
    }
}
