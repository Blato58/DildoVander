using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Conversations;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;

internal static class CustomerAvailabilityTests
{
    internal static void Run()
    {
        var ready = new CustomerAvailabilityEvidence(CustomerLeaseObservation.None, false, true, true,
            CustomerRuntimeObservation.Ready, true, TimeSpan.Zero);
        Expect(ready, CustomerAvailabilityState.Available, true);
        Expect(ready with { ObservationAge = TimeSpan.FromSeconds(9.999) }, CustomerAvailabilityState.Available, true);
        Expect(ready with { ObservationAge = CustomerAvailability.MaximumObservationAge }, CustomerAvailabilityState.Unknown, false);
        Expect(ready with { ObservationAge = null }, CustomerAvailabilityState.Unknown, false);
        Expect(ready with { ObservationAge = TimeSpan.FromTicks(-1) }, CustomerAvailabilityState.Unknown, false);
        Expect(ready with { RuntimeIdentityMatches = false }, CustomerAvailabilityState.Unknown, false);
        Expect(ready with { Runtime = CustomerRuntimeObservation.Unknown }, CustomerAvailabilityState.Unknown, false);
        Expect(ready with { Runtime = (CustomerRuntimeObservation)99 }, CustomerAvailabilityState.Unknown, false);
        Expect(ready with { Lease = (CustomerLeaseObservation)99 }, CustomerAvailabilityState.Unknown, false);
        Expect(ready with { Runtime = CustomerRuntimeObservation.Offline }, CustomerAvailabilityState.Offline, true);
        Expect(ready with { Runtime = CustomerRuntimeObservation.Offline, ObservationAge = null }, CustomerAvailabilityState.Unknown, false);
        Expect(ready with { AdmissionPaused = true }, CustomerAvailabilityState.Paused, true);
        Expect(ready with { EventOpen = false }, CustomerAvailabilityState.Paused, true);
        Expect(ready with { CustomerModeEnabled = false }, CustomerAvailabilityState.Paused, true);

        // Neither stale presence, event closure nor an operator pause releases
        // an existing lease or hides a recovery requirement.
        foreach (var runtime in Enum.GetValues<CustomerRuntimeObservation>())
        foreach (var paused in new[] { false, true })
        foreach (var open in new[] { false, true })
        {
            var value = ready with { Runtime = runtime, AdmissionPaused = paused, EventOpen = open,
                RuntimeIdentityMatches = false, ObservationAge = null };
            Expect(value with { Lease = CustomerLeaseObservation.Occupied }, CustomerAvailabilityState.Busy, true);
            Expect(value with { Lease = CustomerLeaseObservation.Unresolved }, CustomerAvailabilityState.Recovering, true);
        }
        Console.WriteLine("PASS customer availability policy: lease priority, explicit pause, fresh authenticated evidence and conservative stale/restart states");
        RuntimeFreshnessAndRestart();
    }

    private static void Expect(CustomerAvailabilityEvidence evidence, CustomerAvailabilityState state, bool fresh)
    {
        var result = CustomerAvailability.Project(evidence);
        if (result.State != state || result.Fresh != fresh)
        {
            throw new InvalidOperationException($"Availability expected {state}/{fresh}; got {result.State}/{result.Fresh}");
        }
    }

    private static void RuntimeFreshnessAndRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "customer-availability-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = Path.Combine(root, "journal.sqlite")
        }).Build();
        var clock = new AvailabilityClock();
        var boot = Guid.NewGuid().ToString("D");
        try
        {
            using (var journal = new OperationJournal(config, clock))
            {
                journal.SetCustomerMode("device-a", true);
                journal.RegisterCustomerChallenge("device-a", new(1, boot, "fixture-challenge-0123456789", 1, "fixture", "1"));
                State(journal, CustomerAvailabilityState.Unknown);
                if (journal.ObserveCustomerRuntime("device-a", Guid.NewGuid().ToString("D"), true)) throw new InvalidOperationException("Foreign boot accepted");
                if (!journal.ObserveCustomerRuntime("device-a", boot, true)) throw new InvalidOperationException("Current runtime rejected");
                State(journal, CustomerAvailabilityState.Available);
                clock.Advance(10);
                State(journal, CustomerAvailabilityState.Unknown);
                journal.ObserveCustomerRuntime("device-a", boot, false);
                State(journal, CustomerAvailabilityState.Unknown);
                journal.ObserveCustomerRuntime("device-a", boot, true);
                State(journal, CustomerAvailabilityState.Available);
                var newerBoot = Guid.NewGuid().ToString("D");
                journal.RegisterCustomerChallenge("device-a", new(1, newerBoot, "next-challenge-0123456789", 1, "fixture", "1"));
                State(journal, CustomerAvailabilityState.Unknown);
                if (journal.ObserveCustomerRuntime("device-a", boot, true)) throw new InvalidOperationException("Old boot revived presence");
                journal.ObserveCustomerRuntime("device-a", newerBoot, true);
                State(journal, CustomerAvailabilityState.Available);
            }
            using (var reopened = new OperationJournal(config, clock))
            {
                State(reopened, CustomerAvailabilityState.Unknown);
            }
            Console.WriteLine("PASS customer availability SQLite adapter: boot binding, monotonic expiry, explicit non-ready and restart unknown");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private static void State(OperationJournal journal, CustomerAvailabilityState expected)
    {
        var actual = journal.CustomerAvailabilityFor("device-a", eventOpen: true, paused: false).State;
        if (actual != expected) throw new InvalidOperationException($"Runtime state expected {expected}, got {actual}");
    }

    private sealed class AvailabilityClock : TimeProvider
    {
        private long seconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(1_800_000_000 + seconds);
        public override long GetTimestamp() => seconds * 1000;
        public override long TimestampFrequency => 1000;
        internal void Advance(long value) => seconds += value;
    }
}
