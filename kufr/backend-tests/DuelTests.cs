using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.Show;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

internal static class DuelTests
{
    internal static async Task RunAsync()
    {
        RulesFixtures();
        await DuelCommentaryTests.RunAsync();
        using var fixture = new Fixture();
        PersistenceAndReplay(fixture);
        InteractiveAndAuthorization(fixture);
        CancellationAndRecovery(fixture);
        CommentaryClaims();
        NarrationBackpressure();
        await HttpAuthorizationAsync(fixture);
        await ClosedEveningVisibilityAsync();
        Console.WriteLine("Duel deterministic rules, SQLite, replay, grants, timeout, cancellation and HTTP authorization tests passed.");
    }

    private static void RulesFixtures()
    {
        var identity = DuelRules.Snapshot("left", "Archivář", "wand-1");
        Check(identity.Strength == 1 && identity.Cunning == 5 && identity.Defense == 3,
            "versioned wand profile fixture is stable and sums to nine");
        var commitments = Enumerable.Range(1, 3).Select(round => DuelRules.Commit("fixture-seed", round)).ToArray();
        Check(commitments[0] == new DuelCommitment("trick", "strike", 1, -1)
            && commitments[1] == new DuelCommitment("trick", "trick", 0, 1)
            && commitments[2] == new DuelCommitment("shield", "strike", 0, -1),
            "SHA256 counter stream freezes actions and independent jitter");
        var rounds = Enumerable.Range(1, 3).Select(round => DuelRules.Resolve("fixture-seed", round,
            identity, identity with { IdentityId = "right" }, commitments[round - 1], null, null)).ToArray();
        Check(rounds.Select(round => (round.LeftScore, round.RightScore)).SequenceEqual([(4, 2), (5, 6), (5, -2)]),
            "cyclic advantage and profile score fixture");
        var result = DuelRules.Result(rounds, false);
        Check(result.LeftPoints == 2 && result.RightPoints == 1 && result.Winner == "left", "three rounds determine truthful winner");
        Check(rounds.SequenceEqual(Enumerable.Range(1, 3).Select(round => DuelRules.Resolve("fixture-seed", round,
            identity, identity with { IdentityId = "right" }, commitments[round - 1], null, null))), "same seed produces identical narration and score");
        var draws = Enumerable.Range(1, 3).Select(round => DuelRules.Resolve("draw", round, identity, identity,
            new("shield", "shield", 0, 0), "shield", "shield")).ToArray();
        Check(DuelRules.Result(draws, false) is { LeftPoints: 1.5, RightPoints: 1.5, Winner: "draw" }, "draw awards half points");
        foreach (var wand in Enumerable.Range(0, 100).Select(value => DuelRules.Snapshot("id", "Alias", $"wand-{value}")))
        {
            Check(wand.Strength + wand.Cunning + wand.Defense == 9
                && new[] { wand.Strength, wand.Cunning, wand.Defense }.All(value => value is >= 1 and <= 5), "all profiles are balanced");
        }
    }

    private static void PersistenceAndReplay(Fixture fixture)
    {
        var request = fixture.CreateRequest("autoplay");
        var duel = fixture.Store.Create("device-a", request);
        Check(duel.State == "Ready" && duel.Seed is null && duel.Left.WandId == duel.Right.WandId,
            "distinct identities on the same wand accepted; precompletion seed remains private");
        Check(fixture.Store.Create("device-a", request).Id == duel.Id, "duplicate create retains stable ID");
        Reject(409, () => fixture.Store.Create("device-a", request with { Mode = "exhibition" }), "body conflict rejected");
        Reject(400, () => fixture.Store.Create("device-a", request with { RightIdentityId = request.LeftIdentityId }), "same identity rejected");
        Reject(400, () => fixture.Store.Create("device-a", request with { ConsentConfirmed = false }), "consent is required");
        Reject(409, () => fixture.Store.Create("device-b", request), "foreign evening rejected");
        var start = new DuelCommand(duel.Revision, "start-once");
        duel = fixture.Store.Start("device-a", duel.Id, start);
        Check(duel.State == "Running" && fixture.Store.Start("device-a", duel.Id, start).Revision == duel.Revision, "duplicate start is stable");
        fixture.Source.Alias = "Changed elsewhere";
        Check(fixture.Store.Read(duel.Id).Left.DisplayName == "Archivář", "identity snapshot stays immutable after profile change");
        for (var round = 1; round <= 3; round++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(3));
            fixture.Store.Tick();
            var view = fixture.Store.Read(duel.Id);
            Check(view.Rounds.Count == round && view.Rounds.All(value => value.LeftAutomatic && value.RightAutomatic), "autoplay resolves only authoritative due round");
        }
        var completed = fixture.Store.Read(duel.Id);
        Check(completed.State == "Completed" && completed.Seed is { Length: 64 } && completed.Result is not null,
            "completion persists result and reveals reproducibility seed");
        var saved = JsonSerializer.Serialize(completed);
        fixture.Store.Tick();
        Check(JsonSerializer.Serialize(fixture.Reopen().Read(duel.Id)) == saved, "refresh and database reopen never recompute completion");
        Check(fixture.Store.PendingEvents().Count(value => value.Id == duel.Id) == 1, "completion emits exactly one durable event");
        fixture.Store.AcknowledgeEvent(duel.Id);
        Check(fixture.Reopen().PendingEvents().All(value => value.Id != duel.Id), "outbox acknowledgement survives restart");
        fixture.Source.Visible = false;
        Reject(404, () => fixture.Store.Read(duel.Id, "device-a", true), "deleted or disabled source hides old public duel");
        fixture.Source.Visible = true;
    }

    private static void InteractiveAndAuthorization(Fixture fixture)
    {
        var duel = fixture.Store.Create("device-a", fixture.CreateRequest("interactive"));
        Check(duel.State == "AwaitingParticipants", "interactive mode waits for scoped grants");
        Reject(409, () => fixture.Store.Start("device-a", duel.Id, new(duel.Revision, "early")), "no grants means not ready");
        var leftRequest = new DuelGrantRequest("left", duel.Revision, "left-grant");
        var left = fixture.Store.Grant("device-a", duel.Id, leftRequest);
        var right = fixture.Store.Grant("device-a", duel.Id, new("right", left.Duel.Revision, "right-grant"));
        Check(right.Duel.State == "Ready" && left.Token != right.Token && left.Token.Length == 43,
            "two independent high entropy grants prepare interactive duel");
        Check(fixture.Store.Grant("device-a", duel.Id, leftRequest).Token == left.Token, "encrypted grant replay returns original token");
        duel = fixture.Store.Start("device-a", duel.Id, new(right.Duel.Revision, "interactive-start"));
        var choice = new DuelChoiceRequest("left", 1, duel.Revision, "shield", "first-choice");
        Reject(400, () => fixture.Store.Choose(duel.Id, left.Token, choice with { Side = "third" }), "invalid side rejected");
        Reject(403, () => fixture.Store.Choose(duel.Id, right.Token, choice), "opposing grant cannot impersonate side");
        Reject(403, () => fixture.Store.Choose(duel.Id, new string('a', 43), choice), "public gallery knowledge grants no move rights");
        duel = fixture.Store.Choose(duel.Id, left.Token, choice);
        Check(duel.LeftChoiceLocked && !duel.RightChoiceLocked && duel.Rounds.Count == 0,
            "first move locked while opposing action remains undisclosed");
        Check(fixture.Store.Choose(duel.Id, left.Token, choice).Revision == duel.Revision, "double click replay is stable");
        Check(fixture.Store.Choose(duel.Id, left.Token, choice with { OperationKey = "same-choice-new-request" }).Revision == duel.Revision,
            "same locked move does not advance revision or starve opponent");
        Reject(409, () => fixture.Store.Choose(duel.Id, left.Token, choice with { Action = "strike", OperationKey = "change" }), "locked action cannot change");
        Reject(409, () => fixture.Store.Choose(duel.Id, right.Token, choice with { Side = "right", OperationKey = "stale" }), "revision prevents stale opposing submission");
        fixture.Clock.Advance(TimeSpan.FromSeconds(15));
        Reject(409, () => fixture.Store.Choose(duel.Id, right.Token, new("right", 1, duel.Revision, "trick", "late")), "late move cannot replace committed fallback");
        fixture.Store.Tick();
        duel = fixture.Store.Read(duel.Id);
        Check(duel.Rounds[0].LeftAction == "shield" && !duel.Rounds[0].LeftAutomatic && duel.Rounds[0].RightAutomatic,
            "one disconnected phone uses independent precommitted automatic move");
        for (var round = 2; round <= 3; round++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(15));
            fixture.Store.Tick();
        }
        Check(fixture.Store.Read(duel.Id).State == "Completed", "both disconnected phones do not hold show slot indefinitely");

        var foreign = fixture.Store.Create("device-a", fixture.CreateRequest("interactive"));
        var foreignLeft = fixture.Store.Grant("device-a", foreign.Id, new("left", foreign.Revision, "foreign-left"));
        var foreignRight = fixture.Store.Grant("device-a", foreign.Id, new("right", foreignLeft.Duel.Revision, "foreign-right"));
        foreign = fixture.Store.Start("device-a", foreign.Id, new(foreignRight.Duel.Revision, "foreign-start"));
        Reject(403, () => fixture.Store.Choose(foreign.Id, left.Token, new("left", 1, foreign.Revision, "trick", "foreign")), "grant cannot cross duel boundary");
        foreign = fixture.Store.Revoke("device-a", foreign.Id, new("left", foreign.Revision, "revoke"));
        Reject(403, () => fixture.Store.Choose(foreign.Id, foreignLeft.Token, new("left", 1, foreign.Revision, "trick", "revoked")), "revoked grant rejected");
        fixture.Clock.Advance(TimeSpan.FromMinutes(31));
        Reject(403, () => fixture.Store.Choose(foreign.Id, foreignRight.Token, new("right", 1, foreign.Revision, "trick", "expired")), "expired grant rejected");
        fixture.Store.Cancel("device-a", foreign.Id, new(foreign.Revision, "cleanup"));
    }

    private static void CancellationAndRecovery(Fixture fixture)
    {
        var duel = fixture.Store.Create("device-a", fixture.CreateRequest("autoplay"));
        fixture.Slot.Busy = true;
        Reject(409, () => fixture.Store.Start("device-a", duel.Id, new(duel.Revision, "busy")), "active visit blocks duel admission");
        Check(fixture.Store.Read(duel.Id).State == "Ready", "failed admission has no running duel");
        fixture.Slot.Busy = false;
        duel = fixture.Store.Start("device-a", duel.Id, new(duel.Revision, "busy"));
        var cancel = new DuelCommand(duel.Revision, "cancel-once");
        var cancelled = fixture.Store.Cancel("device-a", duel.Id, cancel);
        Check(cancelled.State == "Cancelled" && cancelled.Result is null && fixture.Slot.Current is null,
            "cancel has no winner and releases slot");
        Check(fixture.Store.Cancel("device-a", duel.Id, cancel).Revision == cancelled.Revision, "cancel is idempotent");
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        fixture.Store.Tick();
        Check(fixture.Store.PendingEvents().All(value => value.Id != duel.Id), "cancel never emits completed artifact/event");
        var interrupted = fixture.Store.Create("device-a", fixture.CreateRequest("autoplay"));
        interrupted = fixture.Store.Start("device-a", interrupted.Id, new(interrupted.Revision, "stop-start"));
        fixture.Slot.Current = null;
        fixture.Store.Tick();
        Check(fixture.Store.Read(interrupted.Id) is { State: "Interrupted", Result: null }, "Stop invalidates old duel callbacks");
        var restart = fixture.Store.Create("device-a", fixture.CreateRequest("autoplay"));
        restart = fixture.Store.Start("device-a", restart.Id, new(restart.Revision, "restart-start"));
        var freshRuntime = fixture.Reopen(new FakeSlot());
        freshRuntime.Tick();
        Check(freshRuntime.Read(restart.Id) is { State: "Interrupted", Result: null }, "server restart never silently resumes physical show");
        fixture.Slot.Current = null;
        var exhibition = fixture.Store.Create("device-a", fixture.CreateRequest("exhibition"));
        exhibition = fixture.Store.Start("device-a", exhibition.Id, new(exhibition.Revision, "exhibition-start"));
        for (var round = 0; round < 3; round++) { fixture.Clock.Advance(TimeSpan.FromSeconds(3)); fixture.Store.Tick(); }
        Check(fixture.Store.Read(exhibition.Id).Result?.Exhibition == true
            && fixture.Store.PendingEvents().Single(value => value.Id == exhibition.Id).Exhibition, "fictional exhibition remains explicitly tagged");
    }

    private static async Task HttpAuthorizationAsync(Fixture fixture)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddConfiguration(fixture.Configuration);
        builder.Services.AddSingleton(fixture.Store);
        builder.Services.AddSingleton<ServerAdminAccess>();
        builder.Services.AddRateLimiter(options => options.AddConcurrencyLimiter("keepsake-public", limiter =>
        { limiter.PermitLimit = 10; limiter.QueueLimit = 0; }));
        await using var app = builder.Build();
        // Model the trusted HTTPS termination used in production, solely inside
        // this loopback fixture; production never trusts this test header.
        app.Use((context, next) =>
        {
            if (context.Request.Headers.ContainsKey("X-Duel-Test-Https")) { context.Request.Scheme = "https"; }
            return next(context);
        });
        app.UseRateLimiter();
        app.MapDuels();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        var path = "/v1/show/devices/device-a/duels";
        var response = await client.PostAsJsonAsync(path, fixture.CreateRequest("autoplay"));
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "device/public bearer is not an operator credential");
        client.DefaultRequestHeaders.Authorization = new("Bearer", "duel-test-admin");
        response = await client.PostAsJsonAsync(path, fixture.CreateRequest("autoplay"));
        Check(response.IsSuccessStatusCode, "actual endpoint binds operator request and authenticates admin");
        var duel = await response.Content.ReadFromJsonAsync<DuelView>();
        response = await client.GetAsync($"/v1/show/devices/device-b/duels/{duel!.Id}");
        Check(response.StatusCode == HttpStatusCode.NotFound, "admin endpoint still binds duel to requested device");
        response = await client.GetAsync($"/guest/v1/duels/{duel.Id}");
        Check(response.StatusCode == HttpStatusCode.NotFound, "public route refuses unconfigured/insecure origin");
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Host = "duel.test";
        client.DefaultRequestHeaders.Add("X-Duel-Test-Https", "true");
        response = await client.GetAsync($"/guest/v1/duels/{duel.Id}");
        Check(response.IsSuccessStatusCode, "public origin can read approved snapshots without playing rights");
        response = await client.PostAsJsonAsync($"/guest/v1/duels/{duel.Id}/choices",
            new DuelChoiceRequest("left", 1, duel.Revision, "strike", "no-authority"));
        Check(response.StatusCode == HttpStatusCode.Forbidden, "public origin alone has no right to play");
        client.DefaultRequestHeaders.Authorization = new("Bearer", new string('a', 43));
        response = await client.PostAsJsonAsync($"/guest/v1/duels/{duel.Id}/choices",
            new DuelChoiceRequest("left", 1, duel.Revision, "strike", "guessed-authority"));
        Check(response.StatusCode == HttpStatusCode.Forbidden, "unrecognized side token is rejected at HTTP boundary");
        fixture.Source.Visible = false;
        response = await client.GetAsync($"/guest/v1/duels/{duel.Id}");
        Check(response.StatusCode == HttpStatusCode.NotFound, "public projection respects source withdrawal on every read");
        fixture.Source.Visible = true;
        await app.StopAsync();
    }

    private static void CommentaryClaims()
    {
        using var fixture = new Fixture(commentaryEnabled: true);
        DuelView Complete(string key)
        {
            var duel = fixture.Store.Create("device-a", fixture.CreateRequest("autoplay"));
            duel = fixture.Store.Start("device-a", duel.Id, new(duel.Revision, key));
            for (var round = 0; round < 3; round++)
            {
                fixture.Clock.Advance(TimeSpan.FromSeconds(3));
                fixture.Store.Tick();
            }
            return fixture.Store.Read(duel.Id);
        }
        var first = Complete("first-commentary");
        Check(fixture.Store.ClaimCommentary()?.Id == first.Id && fixture.Store.ClaimCommentary() is null,
            "durable commentary claim is single-dispatch per completed result");
        var restarted = fixture.Reopen();
        restarted.RecoverCommentaryClaims();
        Check(restarted.ClaimCommentary() is null && restarted.Read(first.Id).Commentary is null,
            "uncertain billed commentary is never retried after restart; local fallback stays available");
        var second = Complete("second-commentary");
        _ = fixture.Store.ClaimCommentary();
        var commentary = new DuelCommentary("openai", "fixture-model",
            second.Rounds.Select(round => new DuelRoundCommentary(round.Number, "Approved quip", "Approved mishap")).ToArray());
        fixture.Store.CompleteCommentary(second.Id, new(commentary, new("accepted", "fixture-model", 100, 50, 750)));
        Check(fixture.Reopen().Read(second.Id).Commentary?.Rounds.Count == 3,
            "AI composition persists separately without modifying immutable result scores or revision");
        Check(fixture.Reopen().Read(second.Id).Result == second.Result
            && fixture.Reopen().Read(second.Id).Revision == second.Revision, "commentary cannot mutate result artifact");
    }

    private static void NarrationBackpressure()
    {
        var narrator = new NarratedSlot();
        using var fixture = new Fixture(slot: narrator);
        var duel = fixture.Store.Create("device-a", fixture.CreateRequest("interactive"));
        var left = fixture.Store.Grant("device-a", duel.Id, new("left", duel.Revision, "narration-left"));
        var right = fixture.Store.Grant("device-a", duel.Id, new("right", left.Duel.Revision, "narration-right"));
        duel = fixture.Store.Start("device-a", duel.Id, new(right.Duel.Revision, "narration-start"));
        Check(duel.NarrationMode == "physical_device" && duel.Deadline is null, "start binds recent compatible device and waits for intro");
        fixture.Store.Tick();
        Check(narrator.Enqueued is [{ Round: 0, Scene: "wake" }], "intro is queued once through the device bridge");
        fixture.Clock.Advance(TimeSpan.FromSeconds(8));
        fixture.Store.Tick();
        Check(fixture.Store.Read(duel.Id).Rounds.Count == 0 && narrator.Enqueued.Count == 1,
            "pending playback cannot be overwritten or outrun by three-second round timer");
        narrator.Recent = false;
        narrator.Acknowledge();
        fixture.Store.Tick();
        Check(fixture.Store.Read(duel.Id).Deadline == fixture.Clock.GetUtcNow().AddSeconds(15),
            "interactive choice window starts after exact intro playback acknowledgement");
        for (var round = 1; round <= 3; round++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(15));
            fixture.Store.Tick();
            var waiting = fixture.Store.Read(duel.Id);
            Check(waiting.Rounds.Count == round && waiting.State == "Running" && waiting.Result is null
                && waiting.NarrationMode == "physical_device" && narrator.Enqueued.Count == round + 1,
                "physical binding persists after disconnect; result waits for narrated rounds and finale");
            fixture.Clock.Advance(TimeSpan.FromSeconds(8));
            fixture.Store.Tick();
            Check(fixture.Store.Read(duel.Id).Rounds.Count == round && narrator.Enqueued.Count == round + 1,
                "unacknowledged round cannot advance or overwrite queued narration");
            narrator.Acknowledge();
            fixture.Store.Tick();
        }
        Check(narrator.Enqueued.Count == 5 && narrator.Enqueued[^1] is { Round: 3, Scene: "farewell" }
            && fixture.Store.PendingEvents().Count == 0, "final result speech must drain before completed event");
        narrator.Acknowledge();
        fixture.Store.Tick();
        Check(fixture.Store.Read(duel.Id) is { State: "Completed", Result: not null, NarrationStatus: "playback_confirmed" }
            && fixture.Store.PendingEvents().Count == 1 && narrator.Current is null,
            "all five exact playback acknowledgements complete and release the duel");

        var failedNarrator = new NarratedSlot();
        using var failed = new Fixture(slot: failedNarrator);
        var interrupted = failed.Store.Create("device-a", failed.CreateRequest("autoplay"));
        interrupted = failed.Store.Start("device-a", interrupted.Id, new(interrupted.Revision, "failed-start"));
        failed.Store.Tick();
        failedNarrator.Status = "failed";
        failed.Store.Tick();
        Check(failed.Store.Read(interrupted.Id) is { State: "Interrupted", Result: null, NarrationMode: "physical_device" }
            && failed.Store.PendingEvents().Count == 0, "rejected or expired playback interrupts without silent web downgrade or winner");
        var timeout = failed.Store.Create("device-a", failed.CreateRequest("autoplay"));
        failedNarrator.Status = "idle";
        timeout = failed.Store.Start("device-a", timeout.Id, new(timeout.Revision, "timeout-start"));
        failed.Store.Tick();
        failed.Clock.Advance(TimeSpan.FromSeconds(26));
        failed.Store.Tick();
        Check(failed.Store.Read(timeout.Id).State == "Interrupted", "missing bridge acknowledgement has a bounded server timeout");
    }

    private static async Task ClosedEveningVisibilityAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "duel-evening-visibility-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var clock = new FakeClock();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:DatabasePath"] = Path.Combine(root, "journal.sqlite"),
                ["Backend:Memory:Enabled"] = "false", ["Backend:Memory:LongTermPersonalEnabled"] = "false",
                ["Keepsake:DataRoot"] = Path.Combine(root, "content"),
                ["Keepsake:Enabled"] = "true", ["Keepsake:GenerationEnabled"] = "true",
                ["Keepsake:Origin"] = "https://duel.test", ["Keepsake:PublicDeviceId"] = "device-a",
                ["Devices:0:DeviceId"] = "device-a"
            }).Build();
            using var journal = new OperationJournal(config, clock);
            var memory = new EveningMemoryStore(config, journal, clock);
            var evening = memory.GetStatus("device-a").EveningId;
            var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys")));
            var keepsakes = new KeepsakeStore(config, clock, protection);
            var source = new DuelIdentitySource(keepsakes, memory);
            var duels = new DuelStore(config, clock, source, new FakeSlot(), protection);
            var provider = new FakeKeepsakeProvider();
            var owner = keepsakes.Bootstrap();
            var issued = new List<string>();
            for (var index = 0; index < 3; index++)
            {
                var keepsake = keepsakes.Issue(new(Guid.NewGuid().ToString("D"), "device-a", 1,
                    "wand-1", "Oficiální hůlka", "Synthetic catalog facts", "completed_by_timing", EveningId: evening));
                keepsakes.Claim(keepsakes.IssueClaim(keepsake.Id, "device-a").Token, owner.Token, $"visibility-claim-{index}");
                var job = keepsakes.LeaseNext() ?? throw new InvalidOperationException("Missing identity fixture job.");
                keepsakes.Complete(job, await provider.GenerateAsync(keepsakes.Dispatch(job)!, default));
                issued.Add(keepsake.Id);
            }
            var identities = keepsakes.ListPublicWizardIdentities("device-a", evening);
            var firstIdentity = identities.Single(identity => identity.KeepsakeId == issued[0]).IdentityId;
            var secondIdentity = identities.Single(identity => identity.KeepsakeId == issued[1]).IdentityId;
            var thirdIdentity = identities.Single(identity => identity.KeepsakeId == issued[2]).IdentityId;
            DuelCreateRequest Request(string left, string right) => new(evening, left, right,
                "autoplay", true, Guid.NewGuid().ToString("N"));
            DuelView Complete(DuelCreateRequest request)
            {
                var duel = duels.Create("device-a", request);
                duels.Start("device-a", duel.Id, new(duel.Revision, Guid.NewGuid().ToString("N")));
                for (var round = 0; round < 3; round++)
                {
                    clock.Advance(TimeSpan.FromSeconds(3));
                    duels.Tick();
                }
                return duels.Read(duel.Id);
            }
            var deletedSourceDuel = Complete(Request(firstIdentity, secondIdentity));
            var rotatedEveningDuel = Complete(Request(secondIdentity, thirdIdentity));
            var ready = duels.Create("device-a", Request(secondIdentity, thirdIdentity));

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddConfiguration(config);
            builder.Services.AddSingleton(duels);
            builder.Services.AddRateLimiter(options => options.AddConcurrencyLimiter("keepsake-public", limiter =>
            { limiter.PermitLimit = 10; limiter.QueueLimit = 0; }));
            await using var app = builder.Build();
            // Exercise the public endpoint behind the fixture's trusted HTTPS terminator.
            app.Use((context, next) => { context.Request.Scheme = "https"; return next(context); });
            app.UseRateLimiter();
            app.MapDuels();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            client.DefaultRequestHeaders.Host = "duel.test";
            var path = $"/guest/v1/duels/{deletedSourceDuel.Id}";
            Check((await client.GetAsync(path)).StatusCode == HttpStatusCode.OK, "completed duel public GET is available before closure");
            Check(memory.EndShowEvening("device-a", evening)?.Provisional == false, "real evening lifecycle closes fixture evening");
            Check(source.IsActiveEvening("device-a", evening) && !source.IsOpenEvening("device-a", evening),
                "closed active evening remains publicly visible while admission is closed");
            var afterClose = await client.GetAsync(path);
            Check(afterClose.StatusCode == HttpStatusCode.OK
                && (await afterClose.Content.ReadFromJsonAsync<DuelView>())?.Result == deletedSourceDuel.Result,
                "completed duel public GET retains identical result after EndShowEvening");
            Reject(409, () => duels.Create("device-a", Request(secondIdentity, thirdIdentity)), "closed evening rejects new duel creation");
            Reject(409, () => duels.Start("device-a", ready.Id, new(ready.Revision, "closed-start")), "closed evening rejects ready duel start");

            keepsakes.Delete(issued[0], owner.Token, KeepsakeDeleteScope.All, "visibility-delete");
            Check((await client.GetAsync(path)).StatusCode == HttpStatusCode.NotFound, "identity deletion hides its completed duel even while evening stays active");
            var rotatedPath = $"/guest/v1/duels/{rotatedEveningDuel.Id}";
            Check((await client.GetAsync(rotatedPath)).StatusCode == HttpStatusCode.OK, "other undeleted identities remain visible after closure");
            Check(memory.Create("device-a", Guid.NewGuid(), "visibility-rotate", "body", evening, "Další večer").Status == 201,
                "real evening lifecycle rotates active evening");
            Check((await client.GetAsync(rotatedPath)).StatusCode == HttpStatusCode.NotFound, "active evening rotation hides old completed duel");
            await app.StopAsync();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static void Reject(int status, Action action, string message)
    {
        try { action(); }
        catch (DuelException exception) when (exception.Status == status) { return; }
        throw new InvalidOperationException($"Duel test failed: {message}");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException($"Duel test failed: {message}"); }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "duel-tests-" + Guid.NewGuid().ToString("N"));
        internal readonly FakeClock Clock = new();
        internal readonly FakeIdentities Source = new();
        internal readonly FakeSlot Slot;
        internal readonly IConfiguration Configuration;
        internal readonly IDataProtectionProvider Protection;
        internal readonly DuelStore Store;
        internal Fixture(bool commentaryEnabled = false, FakeSlot? slot = null)
        {
            Slot = slot ?? new FakeSlot();
            Directory.CreateDirectory(root);
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:DatabasePath"] = Path.Combine(root, "duel.sqlite"),
                ["Backend:Admin:CredentialSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("duel-test-admin"))),
                ["Devices:0:DeviceId"] = "device-a", ["Devices:1:DeviceId"] = "device-b",
                ["Keepsake:Enabled"] = "true", ["Keepsake:Origin"] = "https://duel.test",
                ["Keepsake:PublicDeviceId"] = "device-a",
                ["Show:DuelCommentaryEnabled"] = commentaryEnabled.ToString()
            }).Build();
            Protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "keys")));
            Store = Reopen();
        }
        internal DuelStore Reopen(FakeSlot? slot = null) => new(Configuration, Clock, Source, slot ?? Slot, Protection);
        internal DuelCreateRequest CreateRequest(string mode) => new("evening-a", "left", "right", mode, true, Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan elapsed) => now += elapsed;
    }
    private sealed class FakeIdentities : IDuelIdentitySource
    {
        internal string Alias = "Archivář";
        internal bool Visible = true;
        public bool IsActiveEvening(string deviceId, string eveningId) => deviceId == "device-a" && eveningId == "evening-a";
        public bool IsOpenEvening(string deviceId, string eveningId) => IsActiveEvening(deviceId, eveningId);
        public DuelIdentitySnapshot? Read(string id, string eveningId, string deviceId) =>
            Visible && IsActiveEvening(deviceId, eveningId) && id is "left" or "right"
                ? DuelRules.Snapshot(id, Alias, "wand-1") : null;
    }
    private class FakeSlot : IDuelShowSlot
    {
        internal string? Current;
        internal bool Busy;
        public bool TryOccupy(string deviceId, string eveningId, string duelId)
        {
            if (Busy || Current is not null) { return false; }
            Current = duelId;
            return true;
        }
        public bool IsCurrent(string deviceId, string duelId) => Current == duelId;
        public void PublishRound(string deviceId, string duelId, int round, string quip) { }
        public void Release(string deviceId, string duelId) { if (Current == duelId) { Current = null; } }
        public virtual DuelNarrationBinding BindNarration(string deviceId) => new("web_only", "fixture_no_device");
        public virtual bool EnqueueNarration(string deviceId, string duelId, int round, string scene, string wandId, string text) => false;
        public virtual string NarrationState(string deviceId, string duelId) => "idle";
    }
    private sealed class NarratedSlot : FakeSlot
    {
        internal bool Recent = true;
        internal string Status = "idle";
        internal readonly List<(int Round, string Scene, string Text)> Enqueued = [];
        public override DuelNarrationBinding BindNarration(string deviceId) => Recent ? new("physical_device", null) : new("web_only", "device_unavailable");
        public override bool EnqueueNarration(string deviceId, string duelId, int round, string scene, string wandId, string text)
        {
            if (!IsCurrent(deviceId, duelId)) { return false; }
            Enqueued.Add((round, scene, text));
            Status = "pending";
            return true;
        }
        public override string NarrationState(string deviceId, string duelId) => Status;
        internal void Acknowledge() => Status = "idle";
    }
}
