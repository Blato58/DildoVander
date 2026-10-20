using System.Text;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Realtime;
using Microsoft.Extensions.Configuration;

internal static class ConversationMediaLifecycleTests
{
    internal static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kufr-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var clock = TimeProvider.System;
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Backend:DatabasePath"] = Path.Combine(directory, "journal.sqlite") }).Build();
            using var journal = new OperationJournal(config, clock);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource<CostumeImageResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var vision = new CostumeVisionService((_, _) => { started.TrySetResult(); return finish.Task; });
            var store = new ConversationStore(clock, journal, vision: vision);
            var boot = Guid.NewGuid();
            string Create(string device) => store.Create(device, Guid.NewGuid(), Encoding.UTF8.GetBytes(device),
                new(device, boot, 1, PartyWandCatalog.Version, "cs-CZ", new(true, false))).Response!.ConversationId;
            var conversation = Create("a");
            var request = new RealtimeTurnRequest(Guid.NewGuid(), boot, 1, 63, 1, PartyWandCatalog.Version, true,
                "", null, "marin", 1);
            var live = store.BeginRealtimeTurn("a", conversation, Guid.NewGuid(), "stream"u8.ToArray(), request,
                true, default, default, handsFree: true, gptLive: true);
            Check(live.Call is not null, "retained engine admits without legacy runners");
            var imageId = Guid.NewGuid().ToString("D");
            var upload = new CostumeImageUpload(Guid.NewGuid(), boot, 1, 1, PartyWandCatalog.Version, CostumeImageTests.Jpeg())
            { ImageId = imageId, Revision = 1, ExplicitConsent = true, CapturedAt = clock.GetUtcNow() };
            var image = store.SubmitImage("a", conversation, Guid.NewGuid(), upload);
            Check(image.Status == 202, "explicitly consented image can attach during voice");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(store.ReadVisualImage("a", conversation) is null, "pending analysis is not reported as seen");
            store.ReleaseRealtimeTurn(live.Call!, dispatched: true, completed: false);
            var next = store.BeginRealtimeTurn("a", conversation, Guid.NewGuid(), "next"u8.ToArray(),
                request with { ClientTurnId = Guid.NewGuid(), Opening = false }, true, default, default, handsFree: true);
            Check(next.Call is not null, "optional pending vision does not block the next voice turn");
            var pendingWork = store.WaitForVisualWorkAsync("a", conversation);
            store.DeleteVisualContext("a", conversation);
            finish.SetResult(new("A's secret costume", "", "high", 1));
            await pendingWork.WaitAsync(TimeSpan.FromSeconds(5));
            Check(store.ReadVisualContext("a", conversation).Context.Length == 0,
                "manual deletion fences late interpretation and provider context");
            Check(journal.ReadVisualMedia("a", conversation, imageId) is null, "deleted image content erased");
            Check(!journal.ArchiveVisualMedia(new(imageId, "a", conversation, 1, "phone", clock.GetUtcNow(), upload.Jpeg)),
                "upload retry cannot resurrect tombstone");
            var guestB = Create("b");
            Check(store.ReadVisualContext("b", guestB).Context.Length == 0, "guest B receives no guest A image");
            Check(journal.ReadVisualMedia("b", conversation, imageId) is null, "image lookup rechecks device scope");
            var retainedId = Guid.NewGuid().ToString("D");
            Check(journal.ArchiveVisualMedia(new(retainedId, "b", guestB, 1, "phone", clock.GetUtcNow().AddDays(-60), upload.Jpeg)),
                "archive accepts historical captured image");
            journal.PurgeExpired();
            Check(journal.ReadVisualMedia("b", guestB, retainedId) is not null, "transport expiry does not purge media archive");
            Check(!journal.ArchiveVisualMedia(new(retainedId, "a", conversation, 2, "phone", clock.GetUtcNow(), upload.Jpeg)),
                "image identity cannot be rebound across guests");
            store.ReleaseRealtimeTurn(next.Call!, dispatched: true, completed: false);
            Console.WriteLine("Conversation media lifecycle tests passed.");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, recursive: true); }
    }

    private static async Task WaitAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) { await Task.Delay(10, timeout.Token); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
