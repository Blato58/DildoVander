using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Kufr.Backend.Features.CustomerData;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Memory;

internal static partial class AccountIdentityTests
{
    internal static async Task RunCustomerDataLockContentionAsync()
    {
        await using var fixture = await Fixture.StartAsync(customerMemoryFeatures: true);
        using var owner = fixture.Client();
        using var ownerKey = new VirtualPasskey();
        await EnrollAsync(owner, ownerKey, Fixture.Bootstrap, "Customer data lock test owner");
        await CsrfAsync(owner);

        using var customer = fixture.Client();
        using var customerKey = new VirtualPasskey();
        var profile = await CustomerAsync(customer, customerKey);
        var identity = await CsrfAsync(customer);
        var accountId = identity.GetProperty("account").GetProperty("id").GetString()!;
        using var memory = await CustomerMemoryHarness.CreateAsync(fixture.App, accountId, profile);
        SeedAllowedRecord(memory, "lock contention fixture memory", "answer-71");

        var inventory = await memory.Authority.SnapshotAsync(memory.Access);
        var snapshot = await memory.Source.CaptureAsync(inventory);
        var payload = Encoding.UTF8.GetBytes("contention fixture payload");
        var payloadHash = Convert.ToHexString(SHA256.HashData(payload));

        var prelude = await memory.Authority.AcquireAsync(memory.Access);
        ICustomerDataMemoryLease? admission = null;
        CustomerDataGuardScope? sharedScope = null;
        var allowJournalReservation = new ManualResetEventSlim(false);
        var callbackEntered = new ManualResetEventSlim(false);
        var reservationAttempted = new ManualResetEventSlim(false);
        Task<string?>? showAdmission = null;
        JournalCreateReservation? reservation = null;
        Exception? guardedAttemptError = null;
        var deviceId = "lock-test-show-" + accountId[..8];
        var operationId = Guid.NewGuid();
        var bootId = Guid.NewGuid().ToString("D");
        var bodyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("ordinary show create")));

        try
        {
            var acquiredAdmission = await memory.Guarded.AcquireAsync(prelude.Deadline);
            admission = acquiredAdmission;
            var acquiredScope = prelude.AcquireShared(acquiredAdmission);
            sharedScope = acquiredScope;

            // This is the production inverse edge: TryAdmitShowVisit holds the
            // memory gate/transaction while its callback reserves a real journal
            // conversation. The data scope already holds the journal database.
            showAdmission = Task.Run(() => memory.Store.TryAdmitShowVisit(deviceId, () =>
            {
                callbackEntered.Set();
                if (!allowJournalReservation.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Show admission fixture callback was not released.");
                }

                reservationAttempted.Set();
                var created = memory.Journal.ReserveCreate(deviceId, operationId, bodyHash, bootId: bootId);
                reservation = created;
                return created.Conflict ? null : created.Response?.ConversationId;
            }));

            if (!callbackEntered.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Show admission did not reach its journal callback.");
            }

            allowJournalReservation.Set();
            if (!reservationAttempted.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Show admission did not attempt its real journal reservation.");
            }

            // ReserveCreate is blocked behind this scope's immediate journal
            // transaction. Keep the callback holding the real memory gate while
            // both guarded memory operations exercise their bounded gate wait.
            if (showAdmission.Wait(TimeSpan.FromMilliseconds(150)))
            {
                throw new InvalidOperationException("Show journal reservation completed while the customer-data journal guard was held.");
            }

            var boundedAttempts = Stopwatch.StartNew();
            var captureError = CaptureException(() => memory.Guarded.Capture(acquiredScope));
            Check(captureError is CustomerDataAccessException { Status: 503, Code: "customer_data_unavailable" },
                "guarded capture fails closed when show admission owns the memory gate while waiting on journal reservation");

            var writer = new CountingBufferWriter();
            var releaseError = CaptureException(() => memory.Guarded.Release(acquiredScope, snapshot, payload,
                payloadHash, writer));
            Check(releaseError is CustomerDataAccessException { Status: 503, Code: "customer_data_unavailable" }
                && writer.AdvanceCount == 0,
                "guarded release returns bounded unavailable without advancing output under inverse lock contention");
            Check(boundedAttempts.Elapsed < TimeSpan.FromSeconds(2),
                "capture and release each unwind within their bounded memory-gate wait");
        }
        catch (Exception error)
        {
            guardedAttemptError = error;
        }
        finally
        {
            allowJournalReservation.Set();
            try { sharedScope?.Dispose(); }
            finally
            {
                try { admission?.Dispose(); }
                finally { prelude.Dispose(); }
            }
        }

        string? admittedEvening = null;
        Exception? showError = null;
        if (showAdmission is not null)
        {
            try { admittedEvening = await showAdmission.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) { showError = error; }
        }
        allowJournalReservation.Dispose();
        callbackEntered.Dispose();
        reservationAttempted.Dispose();

        if (guardedAttemptError is not null)
        {
            throw new InvalidOperationException("Bounded customer-data lock contention assertion failed.", guardedAttemptError);
        }
        if (showError is not null)
        {
            throw new InvalidOperationException("Show admission did not complete after releasing the customer-data scope.", showError);
        }

        var response = reservation?.Response
            ?? throw new InvalidOperationException("Show journal reservation did not return its persisted response.");
        Check(admittedEvening is { Length: > 0 },
            "ordinary show visit and its journal create reservation complete after reverse-order guard release");
        Check(memory.Store.ReadCapturedShowEvening(deviceId, response.ConversationId)
                == admittedEvening,
            "completed show admission retains its captured evening rather than an orphaned journal create");
        var replay = memory.Journal.FindExistingCreate(deviceId, operationId, bodyHash);
        Check(replay is { Conflict: false, Response: not null }
            && replay.Response.ConversationId == response.ConversationId,
            "ordinary show admission leaves a replayable real journal create paired with its captured visit");

        var freshInventory = await memory.Authority.SnapshotAsync(memory.Access);
        var freshMemory = await memory.Source.CaptureAsync(freshInventory);
        Check(freshMemory.Complete && freshMemory.Items.Any(item => item.Id == memory.MemoryId
                && item.Text == "lock contention fixture memory"),
            "a fresh guarded customer-data memory read succeeds after the show/journal lock edge unwinds");

        Console.WriteLine("PASS customer-data lock contention: real show-memory gate and journal reservation, bounded capture/release failure, reverse guard release, and fresh owner read");
    }
}
