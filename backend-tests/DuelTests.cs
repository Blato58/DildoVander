using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.Show;
using Kufr.Backend.Features.Wands;
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
        DuelBalanceTests.Run();
        StoredV2Compatibility();
        await DuelCommentaryTests.RunAsync();
        using var fixture = new Fixture();
        PersistenceAndReplay(fixture);
        InteractiveAndAuthorization(fixture);
        CancellationAndRecovery(fixture);
        CommentaryClaims();
        NarrationBackpressure();
        PersonalizedRulesAndFlow();
        PersonalizedNarrationFallback();
        await PersonalizedHttpFlowAsync();
        await HttpAuthorizationAsync(fixture);
        await ClosedEveningVisibilityAsync();
        Console.WriteLine("Duel deterministic rules, SQLite, replay, grants, timeout, cancellation and HTTP authorization tests passed.");
    }

    private static WandSpell[] PersonalizedSpells(string suffix) =>
        [new("strike", "Úder " + suffix, "Fiktivní záblesk hvězd."),
         new("trick", "Klam " + suffix, "Fiktivní ozvěna odvádí pozornost."),
         new("shield", "Štít " + suffix, "Fiktivní závoj zachytí kouzlo.")];

    private static void PersonalizedRulesAndFlow()
    {
        var left = DuelRules.PersonalizedSnapshot("left", "Postava A", "generated-a", "Hvězdný proutek", 1, PersonalizedSpells("A"));
        var right = DuelRules.PersonalizedSnapshot("right", "Postava B", "generated-b", "Měsíční ozvěna", 1, PersonalizedSpells("B"));
        Check(left.WandId != right.WandId && left.PhysicalSlot == right.PhysicalSlot,
            "personalized identities are independent of the physical slot");
        foreach (var action in DuelRules.Actions)
        foreach (var opponent in DuelRules.Actions)
        {
            var commitment = DuelRules.Commit("fairness", 1, DuelRules.PersonalizedVersion);
            var first = DuelRules.Resolve("fairness", 1, left, right, commitment, action, opponent, DuelRules.PersonalizedVersion);
            var mirrored = DuelRules.Resolve("fairness", 1, right, left, commitment, opponent, action, DuelRules.PersonalizedVersion);
            Check(first.LeftScore == mirrored.RightScore && first.RightScore == mirrored.LeftScore,
                "v2 scores are symmetric for all nine action pairs regardless of identity/name");
            if (action == opponent) { Check(first.LeftScore == first.RightScore, "same action always ties under v2"); }
        }
        Reject(409, () => DuelRules.PersonalizedSnapshot("id", "alias", "wand", "Name", 7, PersonalizedSpells("x")),
            "invalid physical slot cannot enter a generated duel");
        Reject(409, () => DuelRules.PersonalizedSnapshot("id", "alias", "wand", "Name", 1,
            [new("strike", "same", "desc"), new("strike", "other", "desc"), new("shield", "last", "desc")]),
            "each action has exactly one named spell");

        using var fixture = new Fixture();
        fixture.Source.Personalized = true;
        fixture.Source.Mixed = true;
        Reject(409, () => fixture.Store.Create("device-a", fixture.CreateRequest("interactive")),
            "mixed legacy/generated assignments cannot silently use legacy scoring");
        fixture.Source.Mixed = false;
        var request = fixture.CreateRequest("interactive");
        var duel = fixture.Store.Create("device-a", request);
        Check(duel.RulesVersion == DuelRules.CharacterVersion && duel.RequiresParticipantConsent,
            "generated wand duel pins the v3 rules and explicit consent flow");
        var grantLeft = fixture.Store.Grant("device-a", duel.Id, new("left", duel.Revision, "v2-left-grant"));
        var grantRight = fixture.Store.Grant("device-a", duel.Id, new("right", grantLeft.Duel.Revision, "v2-right-grant"));
        Check(grantRight.Duel.State == "AwaitingParticipants", "operator-issued grants alone do not imply participant consent");
        Reject(409, () => fixture.Store.Start("device-a", duel.Id, new(grantRight.Duel.Revision, "v2-premature")), "cannot start before visitors join");
        Reject(400, () => fixture.Store.Join(duel.Id, grantLeft.Token, new("left", false, "decline")), "visitor consent must be affirmative");
        Reject(403, () => fixture.Store.Join(duel.Id, grantRight.Token, new("left", true, "impersonate")), "join is side-grant scoped");
        var joinLeft = new DuelJoinRequest("left", true, "v2-left-join");
        duel = fixture.Store.Join(duel.Id, grantLeft.Token, joinLeft);
        Check(duel.LeftJoined && !duel.RightJoined && duel.State == "AwaitingParticipants", "first visitor waits for opponent");
        Check(fixture.Store.Join(duel.Id, grantLeft.Token, joinLeft).Revision == duel.Revision, "join retry is replay safe");
        duel = fixture.Store.Join(duel.Id, grantRight.Token, new("right", true, "v2-right-join"));
        Check(duel.State == "Ready" && duel.RightJoined, "two consented independent grants prepare duel");
        var rotated = fixture.Store.Grant("device-a", duel.Id, new("left", duel.Revision, "v2-left-rotated"));
        Check(rotated.Duel.State == "AwaitingParticipants" && !rotated.Duel.LeftJoined,
            "rotating a grant withdraws that participant's consent");
        Reject(403, () => fixture.Store.Join(duel.Id, grantLeft.Token, joinLeft), "stale grant cannot replay prior join");
        duel = fixture.Store.Join(duel.Id, rotated.Token, joinLeft);
        duel = fixture.Store.Start("device-a", duel.Id, new(duel.Revision, "v2-start"));
        Check(duel.NarrationMode == "web_only" && duel.Left.Strength == 3 && duel.Right.Defense == 3,
            "device-free start is explicitly web only with equal bounded server stats");
        var opening = duel.Revision;
        var firstChoice = new DuelChoiceRequest("left", 1, opening, "strike", "v2-choice-left");
        duel = fixture.Store.Choose(duel.Id, rotated.Token, firstChoice);
        var unrevealed = JsonSerializer.Serialize(duel);
        Check(duel.LeftChoiceLocked && duel.Rounds.Count == 0 && !unrevealed.Contains("\"LeftChoice\":")
            && !unrevealed.Contains("\"Commitments\":") && duel.Seed is null,
            "public view exposes locked state without chosen actions, seed or commitments");
        duel = fixture.Store.Choose(duel.Id, grantRight.Token, new("right", 1, opening, "trick", "v2-choice-right"));
        Check(duel.RightChoiceLocked, "both phones may submit from one observed round revision");
        fixture.Store.Tick();
        duel = fixture.Store.Read(duel.Id);
        Check(duel.Rounds[0] is { LeftAction: "strike", RightAction: "trick", LeftAutomatic: false, RightAutomatic: false },
            "both committed actions reveal only on authoritative resolution");
        Reject(409, () => fixture.Store.Choose(duel.Id, rotated.Token, new("left", 1, opening, "shield", "v2-old-round")),
            "old round cannot accept new actions");
        duel = fixture.Store.Choose(duel.Id, rotated.Token, new("left", 2, duel.Revision, "shield", "v2-round2"));
        fixture.Clock.Advance(TimeSpan.FromSeconds(15));
        fixture.Reopen().Tick();
        duel = fixture.Store.Read(duel.Id);
        Check(duel.Rounds[1].RightAutomatic && !duel.Rounds[1].LeftAutomatic, "restart preserves a committed move and deterministic timeout");
        fixture.Clock.Advance(TimeSpan.FromSeconds(15));
        fixture.Store.Tick();
        duel = fixture.Store.Read(duel.Id);
        Check(duel.State == "Completed" && duel.Seed is not null, "personalized duel completes exactly three rounds");
        var replay = duel.Rounds.Select(round => DuelRules.Resolve(duel.Seed!, round.Number, duel.Left, duel.Right,
            DuelRules.Commit(duel.Seed!, round.Number, duel.RulesVersion), round.LeftAutomatic ? null : round.LeftAction,
            round.RightAutomatic ? null : round.RightAction, duel.RulesVersion)).ToArray();
        Check(replay.SequenceEqual(duel.Rounds) && DuelRules.Result(replay, false) == duel.Result,
            "immutable v2 seed, snapshots and rules reproduce every score and final result");
        fixture.Store.PurgeIdentity("left");
        Reject(404, () => fixture.Reopen().Read(duel.Id), "identity deletion erases personalized duel snapshots");
        Check(fixture.Store.PendingEvents().All(item => item.Id != duel.Id), "deletion erases duel completion outbox");
        Reject(409, () => fixture.Store.Create("device-a", request), "deletion tombstone fences stale source capture and create replay");
    }

    private static void PersonalizedNarrationFallback()
    {
        var narrator = new NarratedSlot();
        using var fixture = new Fixture(slot: narrator);
        fixture.Source.Personalized = true;
        var duel = fixture.Store.Create("device-a", fixture.CreateRequest("autoplay"));
        duel = fixture.Store.Start("device-a", duel.Id, new(duel.Revision, "v2-narrated-start"));
        fixture.Store.Tick();
        Check(narrator.Enqueued[0].Text.Contains("Hůlka left") && narrator.Enqueued[0].WandId == PartyWandCatalog.Available(63)[0].StableId,
            "personalized narration names wand but routes effects through trusted physical slot mapping");
        narrator.Status = "failed";
        fixture.Store.Tick();
        duel = fixture.Store.Read(duel.Id);
        Check(duel.State == "Running" && duel.NarrationMode == "web_only"
            && duel.NarrationUnavailableReason == "device_playback_unconfirmed"
            && duel.NarrationStatus == "web_only_no_spoken_confirmation", "missing device acknowledgement continues honestly on web");
        for (var i = 0; i < 3; i++) { fixture.Clock.Advance(TimeSpan.FromSeconds(3)); fixture.Store.Tick(); }
        Check(fixture.Store.Read(duel.Id).State == "Completed", "device loss does not block web-only three-round result");
    }

    private static async Task PersonalizedHttpFlowAsync()
    {
        using var fixture = new Fixture();
        fixture.Source.Personalized = true;
        var duel = fixture.Store.Create("device-a", fixture.CreateRequest("interactive"));
        var leftGrant = fixture.Store.Grant("device-a", duel.Id, new("left", duel.Revision, "http-left-grant"));
        var rightGrant = fixture.Store.Grant("device-a", duel.Id, new("right", leftGrant.Duel.Revision, "http-right-grant"));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddConfiguration(fixture.Configuration);
        builder.Services.AddSingleton(fixture.Store);
        builder.Services.AddRateLimiter(options => options.AddConcurrencyLimiter("keepsake-public", limiter =>
        { limiter.PermitLimit = 10; limiter.QueueLimit = 0; }));
        await using var app = builder.Build();
        app.Use((context, next) => { context.Request.Scheme = "https"; return next(context); });
        app.UseRateLimiter();
        app.MapDuels();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var leftClient = new HttpClient { BaseAddress = new Uri(address) };
        using var rightClient = new HttpClient { BaseAddress = new Uri(address) };
        leftClient.DefaultRequestHeaders.Host = rightClient.DefaultRequestHeaders.Host = "duel.test";
        leftClient.DefaultRequestHeaders.Authorization = new("Bearer", leftGrant.Token);
        rightClient.DefaultRequestHeaders.Authorization = new("Bearer", rightGrant.Token);
        var path = $"/guest/v1/duels/{duel.Id}";
        var wrong = await leftClient.PostAsJsonAsync(path + "/join", new DuelJoinRequest("right", true, "wrong-side"));
        Check(wrong.StatusCode == HttpStatusCode.Forbidden, "HTTP join refuses opposite participant grant");
        var joins = await Task.WhenAll(
            leftClient.PostAsJsonAsync(path + "/join", new DuelJoinRequest("left", true, "http-left-join")),
            rightClient.PostAsJsonAsync(path + "/join", new DuelJoinRequest("right", true, "http-right-join")));
        Check(joins.All(response => response.IsSuccessStatusCode), "two separate HTTP clients can independently join and consent");
        duel = fixture.Store.Read(duel.Id);
        duel = fixture.Store.Start("device-a", duel.Id, new(duel.Revision, "http-start"));
        for (var round = 1; round <= 3; round++)
        {
            var choices = await Task.WhenAll(
                leftClient.PostAsJsonAsync(path + "/choices", new DuelChoiceRequest("left", round, duel.Revision, "strike", "http-left-" + round)),
                rightClient.PostAsJsonAsync(path + "/choices", new DuelChoiceRequest("right", round, duel.Revision, "shield", "http-right-" + round)));
            Check(choices.All(response => response.IsSuccessStatusCode), "both HTTP phones commit from same snapshot without revision starvation");
            var hidden = await leftClient.GetFromJsonAsync<DuelView>(path);
            Check(hidden!.Rounds.Count == round - 1 && hidden.LeftChoiceLocked && hidden.RightChoiceLocked,
                "both-client projection remains hidden until backend round transition");
            fixture.Store.Tick();
            duel = (await rightClient.GetFromJsonAsync<DuelView>(path))!;
        }
        Check(duel.State == "Completed" && duel.Result?.Winner == "right" && duel.Rounds.All(round => !round.LeftAutomatic && !round.RightAutomatic),
            "two clients finish all three named-spell rounds with authoritative reproducible winner");
        await app.StopAsync();
    }

    private static void StoredV2Compatibility()
    {
        using var fixture = new Fixture();
        fixture.Source.Personalized = true;
        var left = DuelRules.PersonalizedSnapshot("left", "Archivář", "generated-left", "Hůlka levá", 1, PersonalizedSpells("left"));
        var right = DuelRules.PersonalizedSnapshot("right", "Písař", "generated-right", "Hůlka pravá", 1, PersonalizedSpells("right"));
        var seed = "fixture-seed";
        var commitments = Enumerable.Range(1, 3).Select(round => DuelRules.Commit(seed, round, DuelRules.PersonalizedVersion)).ToArray();
        Check(commitments.SequenceEqual([new("strike", "trick", -1, -1), new("trick", "shield", 0, 0), new("strike", "strike", -1, -1)]),
            "frozen v2 SHA stream remains unchanged after v3 admission");
        var rounds = Enumerable.Range(1, 3).Select(round => DuelRules.Resolve(seed, round,
            left, right, commitments[round - 1], null, null, DuelRules.PersonalizedVersion)).ToList();
        Check(rounds.Select(round => (round.LeftScore, round.RightScore)).SequenceEqual([(4, 0), (5, 1), (2, 2)]),
            "frozen v2 power and shared jitter remain unchanged");
        var duel = new DuelDocument { Id = Guid.NewGuid().ToString(), DeviceId = "device-a", EveningId = "evening-a",
            Seed = seed, RulesVersion = DuelRules.PersonalizedVersion, Mode = "autoplay", State = "Completed",
            Left = left, Right = right, Commitments = commitments, Rounds = rounds, Result = DuelRules.Result(rounds, false) };
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + fixture.Configuration["Backend:DatabasePath"]))
        {
            db.Open(); using var insert = db.CreateCommand();
            insert.CommandText = "INSERT INTO show_duels VALUES($id,$device,$evening,0,'Completed',$document)";
            insert.Parameters.AddWithValue("$id", duel.Id); insert.Parameters.AddWithValue("$device", duel.DeviceId);
            insert.Parameters.AddWithValue("$evening", duel.EveningId);
            insert.Parameters.AddWithValue("$document", JsonSerializer.Serialize(duel, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            insert.ExecuteNonQuery();
        }
        var restored = fixture.Reopen().Read(duel.Id);
        Check(restored.RulesVersion == DuelRules.PersonalizedVersion && restored.Rounds.SequenceEqual(rounds)
            && restored.Result == duel.Result && restored.Left.Strength == 3,
            "database reopen preserves old v2 snapshots and result without migration or recomputation");
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
        internal bool Personalized;
        internal bool Mixed;
        public bool IsActiveEvening(string deviceId, string eveningId) => deviceId == "device-a" && eveningId == "evening-a";
        public bool IsOpenEvening(string deviceId, string eveningId) => IsActiveEvening(deviceId, eveningId);
        public DuelIdentitySnapshot? Read(string id, string eveningId, string deviceId) =>
            Visible && IsActiveEvening(deviceId, eveningId) && id is "left" or "right"
                ? Personalized && (!Mixed || id == "left") ? DuelRules.PersonalizedSnapshot(id, Alias, "generated-" + id,
                    "Hůlka " + id, 1, PersonalizedSpells(id)) : DuelRules.Snapshot(id, Alias, "wand-1") : null;
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
        internal readonly List<(int Round, string Scene, string Text, string WandId)> Enqueued = [];
        public override DuelNarrationBinding BindNarration(string deviceId) => Recent ? new("physical_device", null) : new("web_only", "device_unavailable");
        public override bool EnqueueNarration(string deviceId, string duelId, int round, string scene, string wandId, string text)
        {
            if (!IsCurrent(deviceId, duelId)) { return false; }
            Enqueued.Add((round, scene, text, wandId));
            Status = "pending";
            return true;
        }
        public override string NarrationState(string deviceId, string duelId) => Status;
        internal void Acknowledge() => Status = "idle";
    }
}
