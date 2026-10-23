using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Memory;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

internal static class CustomerMemoryWriteLeaseTests
{
    internal static async Task RunAsync()
    {
        using var fixture = new Fixture();
        await LeaseSerializesCustomerMemoryWritesAsync(fixture);
        CheckDatabasePathIsReadOnlyStorePath(fixture);
        Console.WriteLine("PASS customer memory write lease: bounded contention, cancellation, idempotent release, reacquisition, and file-backed database path");
    }

    private static async Task LeaseSerializesCustomerMemoryWritesAsync(Fixture fixture)
    {
        using var first = await fixture.Service.TryAcquireCustomerMemoryWritesAsync(CancellationToken.None)
            ?? throw new InvalidOperationException("Could not acquire the first customer-memory write lease.");

        var contended = await fixture.Service.TryAcquireCustomerMemoryWritesAsync(CancellationToken.None);
        Check(contended is null, "a concurrent customer-memory writer receives no lease while held");

        using var cancellation = new CancellationTokenSource();
        var waiting = fixture.Service.TryAcquireCustomerMemoryWritesAsync(cancellation.Token);
        await Task.Yield();
        cancellation.Cancel();
        var canceled = false;
        try
        {
            using var unexpected = await waiting;
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        Check(canceled, "cancellation while waiting propagates to the caller");
        Check(await fixture.Service.TryAcquireCustomerMemoryWritesAsync(CancellationToken.None) is null,
            "a canceled waiter does not release the first writer's lease");

        first.Dispose();
        first.Dispose();
        using var next = await fixture.Service.TryAcquireCustomerMemoryWritesAsync(CancellationToken.None)
            ?? throw new InvalidOperationException("The lease was not released after idempotent disposal.");
    }

    private static void CheckDatabasePathIsReadOnlyStorePath(Fixture fixture)
    {
        var expected = Path.GetFullPath(fixture.DatabasePath + ".evening.sqlite");
        Check(string.Equals(fixture.Store.CustomerMemoryDatabasePath, expected, StringComparison.Ordinal),
            "the read-only getter returns the canonical backing SQLite file path");
        Check(File.Exists(fixture.Store.CustomerMemoryDatabasePath),
            "the getter identifies the actual initialized file-backed archive");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Customer memory write lease test failed: " + message);
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "kufr-customer-memory-lease-" + Guid.NewGuid().ToString("N"));
        internal string DatabasePath => Path.Combine(DirectoryPath, "archive.sqlite");
        internal OperationJournal Journal { get; }
        internal EveningMemoryStore Store { get; }
        internal HybridMemoryService Service { get; }
        private readonly OpenAiVectorMemoryProvider provider;

        internal Fixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:DatabasePath"] = DatabasePath,
                ["Backend:Memory:Enabled"] = "true",
                ["Backend:Memory:AppNamespace"] = "lease-tests",
                ["Backend:Memory:Environment"] = "Development"
            }).Build();
            Journal = new OperationJournal(configuration, TimeProvider.System);
            Store = new EveningMemoryStore(configuration, Journal, TimeProvider.System);
            var options = HybridMemoryOptions.Read(configuration, Journal);
            provider = new OpenAiVectorMemoryProvider(new HttpClient(new OfflineHandler()), null, options, ownsHttp: true);
            Service = new HybridMemoryService(Store, provider, options, TimeProvider.System);
        }

        public void Dispose()
        {
            provider.Dispose();
            Journal.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Customer-memory write-lease tests must not call external providers.");
    }
}
