using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.Show;
using Kufr.Backend.Features.Wands;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

// Actual durable feature boundaries with synthetic providers and no actuators.
internal static class GeneratedWandFlowTests
{
    internal static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "generated-wand-flow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:DatabasePath"] = Path.Combine(directory, "backend.sqlite"),
                ["Backend:Memory:Enabled"] = "false", ["Backend:Memory:LongTermPersonalEnabled"] = "false",
                ["Keepsake:DataRoot"] = Path.Combine(directory, "keepsakes"),
                ["Keepsake:Enabled"] = "true", ["Keepsake:GenerationEnabled"] = "true",
                ["Keepsake:Origin"] = "https://wand.test", ["Keepsake:PublicDeviceId"] = "device",
                ["Devices:0:DeviceId"] = "device"
            }).Build();
            var clock = new Clock();
            using var journal = new OperationJournal(config, clock);
            var memory = new EveningMemoryStore(config, journal, clock);
            var evening = memory.GetStatus("device").EveningId;
            var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory, "keys")));
            var characters = new CharacterProfileStore(Path.Combine(directory, "characters"));
            var wands = new WandGenerationStore(config, clock);
            var generation = new WandGenerationService(wands, new FakeWandProvider());
            var keepsakes = new KeepsakeStore(config, clock, protection);
            var keeper = keepsakes.Bootstrap();
            var issued = new List<(string Account, CharacterProfile Profile, CharacterConversationSnapshot Captured,
                GeneratedWandSnapshot Wand, string Keepsake, WizardIdentityCard Card)>();

            foreach (var (account, name) in new[] { ("alice", "Raven"), ("bob", "Lumen") })
            {
                var profile = await characters.SaveAsync(account, new(0,
                    new(name, "Original fictional universe", Description: "A fictional star keeper", Language: "cs"), OpenAiConsent: true));
                var enrichmentJob = (await characters.ClaimAsync())!;
                var enrichment = new CharacterEnrichment(name, "Original fictional universe", "Fictional keeper of lost stars.",
                    [], ["Ask about a lost constellation"], [], false, [], "fake-model", "fake-context-v1",
                    clock.GetUtcNow().ToUnixTimeSeconds(), profile.Revision, "synthetic");
                Check(await characters.CompleteAsync(enrichmentJob, enrichment, null), "synthetic enrichment completes real profile job");
                var captured = (await characters.CaptureAsync(account, profile.ProfileId, profile.Revision))!;
                Check(captured.Enrichment?.RecognizedName == name, "generation receives immutable enriched character snapshot");
                var operation = "selection-" + account;
                wands.Begin(account, operation, captured, 0b100001, true);
                var candidates = await generation.GenerateAsync(account, operation, async token =>
                {
                    var current = await characters.ReadAsync(account, token);
                    return current?.ProfileId == captured.ProfileId && current.Revision == captured.Revision;
                });
                Check(candidates.Status == "ready" && candidates.Candidates.Length == 2, "personalized candidates produced for eligible slots");
                var wand = wands.Select(account, operation, profile.ProfileId, profile.Revision, 1, 0b100001);
                Check(wands.LatestBonded(account, profile.ProfileId, profile.Revision) is null,
                    "AI selection alone does not claim a physically completed wand bond");
                var snapshot = new KeepsakeIssueSnapshot("outcome-" + account, "device", wand.PhysicalSlot,
                    wand.WandId, wand.Name, wand.Lore, "synthetic_fixture_timing_evidence", "generated-wand-v1",
                    EveningId: evening, SourceConversationId: Guid.NewGuid().ToString("D"), GeneratedWand: wand.ToPublic());
                var issuedKeepsake = keepsakes.Issue(snapshot, new(account, "customer-session-" + account));
                Check(wands.LatestBonded(account, profile.ProfileId, profile.Revision)?.WandId == wand.WandId
                    && wands.LatestBonded("foreign-account", profile.ProfileId, profile.Revision) is null,
                    "only an issued completed outcome owned by the account exposes its character wand bond");
                Check(wands.LatestBonded(account, profile.ProfileId, profile.Revision + 1)?.WandId == wand.WandId,
                    "normal profile edits preserve the issued bond until another valid selection");
                keepsakes.Claim(keepsakes.IssueClaim(issuedKeepsake.Id, "device").Token, keeper.Token, "claim-" + account);
                var artworkJob = keepsakes.LeaseNext() ?? throw new InvalidOperationException("Missing selected-wand artwork job.");
                Check(artworkJob.Kind == "wand_artwork", "selected-wand illustration is dispatched before keepsake story");
                var artworkRequest = keepsakes.Dispatch(artworkJob)!;
                Check(keepsakes.Complete(artworkJob, await new FakeKeepsakeProvider().GenerateAsync(artworkRequest, default)),
                    "canonical image publishes independently of later keepsake story");
                var earlyArtwork = keepsakes.FindOwnedWandArtwork(account, wand.WandId);
                Check(earlyArtwork?.AssetId is not null && keepsakes.Read(issuedKeepsake.Id, keeper.Token).Editions.Count == 0,
                    "character image is available before a keepsake edition exists");
                var job = keepsakes.LeaseNext() ?? throw new InvalidOperationException("Missing generated keepsake story job.");
                var request = keepsakes.Dispatch(job)!;
                Check(request.CanonicalArtworkAssetId == earlyArtwork!.AssetId, "keepsake references canonical selected-wand image");
                Check(request.Snapshot.GeneratedWand?.WandId == wand.WandId
                    && request.Snapshot.OfficialName == wand.Name, "keepsake dispatch consumes canonical selected identity");
                var output = await new FakeKeepsakeProvider().GenerateAsync(request, default);
                Check(output.Story?.Design == OpenAiKeepsakeProvider.CanonicalDesign(wand.ToPublic()),
                    "digital design reuses selected wand instead of creating another appearance");
                var contradictory = new WandDesignSpec("different shape", "different handle", "different ornament", "different material", "different theme");
                var partialStory = new KeepsakeStory("A synthetic partial story", string.Join(" ", Enumerable.Repeat("fictional star", 110)), contradictory);
                var stageResponse = JsonSerializer.Serialize(new { status = "completed", model = "fake-stage",
                    output = new[] { new { type = "message", content = new[] { new { type = "output_text",
                        text = JsonSerializer.Serialize(partialStory, new JsonSerializerOptions(JsonSerializerDefaults.Web)) } } } } });
                Check(request.RecordStage!("story", "completed", stageResponse), "production stage hook accepts synthetic story");
                Check(keepsakes.Read(issuedKeepsake.Id, keeper.Token).Editions.Single().Story.Design
                    == OpenAiKeepsakeProvider.CanonicalDesign(wand.ToPublic()),
                    "partial story stage cannot publish a contradictory design before image completion");
                Check(keepsakes.Complete(job, output with { Story = output.Story! with { Design = contradictory } }),
                    "completed keepsake publishes immutable generated wand");
                var view = keepsakes.Read(issuedKeepsake.Id, keeper.Token);
                Check(keepsakes.Issue(view.Snapshot, new(account, "customer-session-" + account)).Id == issuedKeepsake.Id,
                    "replaying deserialized generated snapshot with spell arrays reuses the original keepsake");
                Check(view.Editions.Single().Story.Design == OpenAiKeepsakeProvider.CanonicalDesign(wand.ToPublic()),
                    "final store ingestion enforces canonical design independently of the provider");
                var illustrated = keepsakes.FindOwnedWandArtwork(account, wand.WandId);
                Check(illustrated?.Locator == issuedKeepsake.Id
                    && illustrated.AssetId == view.Editions.Single().ArtworkAssetId,
                    "original character owner can reuse the exact saved image in profile and result");
                Check(keepsakes.FindOwnedWandArtwork("unrelated-account", wand.WandId) is null,
                    "another account cannot resolve an owner's generated wand artwork");
                Check(keepsakes.GetPublicWizardIdentity(view.WizardIdentities!.Single().IdentityId, evening, "device") is null,
                    "private generated wand requires explicit owner publication before public card or duel admission");
                view = keepsakes.SetWandPublication(issuedKeepsake.Id,
                    KeepsakeActor.ForAccount(account, "account-session-" + account, () => true), true,
                    view.Editions.Single().Id, view.Revision);
                var card = keepsakes.GetPublicWizardIdentity(view.WizardIdentities!.Single().IdentityId, evening, "device")!;
                Check(card.GeneratedWand?.WandId == wand.WandId && card.GeneratedWand.Name == wand.Name
                    && card.GeneratedWand.Spells.Select(spell => spell.Name).SequenceEqual(wand.Spells.Select(spell => spell.Name)),
                    "public permitted identity and keepsake retain the chosen name and all three spells");
                var publicJson = JsonSerializer.Serialize(card);
                Check(!publicJson.Contains(profile.ProfileId) && !publicJson.Contains(candidates.SessionId)
                    && !publicJson.Contains("fictional star keeper"), "public card excludes binding ids and private description");
                issued.Add((account, profile, captured, wand, issuedKeepsake.Id, card));
            }
            Check(issued[0].Wand.PhysicalSlot == issued[1].Wand.PhysicalSlot
                && issued[0].Wand.WandId != issued[1].Wand.WandId && issued[0].Wand.Name != issued[1].Wand.Name,
                "two characters receive distinct generated identities using the same physical compartment");
            var source = new DuelIdentitySource(keepsakes, memory);
            var slot = new WebSlot();
            var duels = new DuelStore(config, clock, source, slot, protection);
            var requestDuel = new DuelCreateRequest(evening, issued[0].Card.IdentityId, issued[1].Card.IdentityId,
                "interactive", true, "generated-flow-duel");
            var duel = duels.Create("device", requestDuel);
            Check(duel.RulesVersion == DuelRules.CharacterVersion && duel.Left.WandName == issued[0].Wand.Name
                && duel.Right.WandName == issued[1].Wand.Name && duel.Left.Spells!.Length == 3,
                "existing duel identity source resolves real published generated assignments under versioned character rules");
            var leftArtworkId = keepsakes.FindOwnedWandArtwork("alice", issued[0].Wand.WandId)!.AssetId!;
            var rightArtworkId = keepsakes.FindOwnedWandArtwork("bob", issued[1].Wand.WandId)!.AssetId!;
            Check(duel.Left.ArtworkUrl?.EndsWith("/" + leftArtworkId) == true
                && duel.Right.ArtworkUrl?.EndsWith("/" + rightArtworkId) == true && leftArtworkId != rightArtworkId,
                "duel faces two exact independently saved canonical illustrations");
            var left = duels.Grant("device", duel.Id, new("left", duel.Revision, "flow-grant-left"));
            var right = duels.Grant("device", duel.Id, new("right", left.Duel.Revision, "flow-grant-right"));
            duel = duels.Join(duel.Id, left.Token, new("left", true, "flow-join-left"));
            duel = duels.Join(duel.Id, right.Token, new("right", true, "flow-join-right"));
            duel = duels.Start("device", duel.Id, new(duel.Revision, "flow-start"));
            Check(duel.NarrationMode == "web_only", "no device is explicitly web only");
            foreach (var round in Enumerable.Range(1, 3))
            {
                var revision = duel.Revision;
                duel = duels.Choose(duel.Id, left.Token, new("left", round, revision, "strike", "flow-left-" + round));
                Check(duel.Rounds.Count == round - 1 && duel.LeftChoiceLocked && duel.Seed is null,
                    "first participant choice remains hidden until both committed");
                duel = duels.Choose(duel.Id, right.Token, new("right", round, revision, "trick", "flow-right-" + round));
                duels.Tick();
                duel = duels.Read(duel.Id, publicView: true);
                Check(duel.Rounds.Count == round && duel.Rounds[^1].LeftScore > duel.Rounds[^1].RightScore,
                    "both clients commit independently and server resolves canonical actions");
            }
            Check(duel.State == "Completed" && duel.Result?.Winner == "left" && slot.Rounds == 3,
                "three named-spell rounds produce the backend-owned result");
            var aliceActor = KeepsakeActor.ForAccount("alice", "account-session-alice", () => true);
            var outsiderActor = KeepsakeActor.ForAccount("outsider", "account-session-outsider", () => true);
            Check(duels.AccountHistory(keepsakes.OwnedBattleIdentities(aliceActor)).Single().Id == duel.Id
                && duels.AccountHistory(keepsakes.OwnedBattleIdentities(outsiderActor)).Count == 0,
                "saved battle history requires original account-owned identity, not shared physical compartment");
            var aliceView = keepsakes.Read(issued[0].Keepsake, aliceActor);
            var approvedEdition = aliceView.Editions.Single().Id;
            try
            {
                keepsakes.SetWandPublication(issued[0].Keepsake, outsiderActor, true, approvedEdition, aliceView.Revision);
                throw new InvalidOperationException("Other account approved private fiction");
            }
            catch (KeepsakeException exception) when (exception.Status is 403 or 404) { }
            aliceView = keepsakes.SetWandPublication(issued[0].Keepsake, aliceActor, false, approvedEdition, aliceView.Revision);
            Check(keepsakes.GetPublicWizardIdentity(issued[0].Card.IdentityId, evening, "device") is null
                && keepsakes.ReadEveningGallery("device", evening).Items.All(item => item.Locator != issued[0].Keepsake),
                "withdrawing edition publication hides both public identity and gallery fiction");
            var storedBattle = duels.AccountHistory(keepsakes.OwnedBattleIdentities(aliceActor)).Single();
            var withdrawnBattle = keepsakes.ProjectBattleArtwork(storedBattle);
            Check(withdrawnBattle.Left.ArtworkUrl is null && withdrawnBattle.Right.ArtworkUrl is not null
                && storedBattle.Left.ArtworkUrl is not null && withdrawnBattle.Result == storedBattle.Result,
                "history projection removes withdrawn artwork while preserving immutable stored combat snapshots");
            keepsakes.SetWandPublication(issued[0].Keepsake, aliceActor, true, approvedEdition, aliceView.Revision);
            var oldName = issued[0].Wand.Name;
            await characters.SaveAsync("alice", new(issued[0].Profile.Revision,
                issued[0].Profile.UserInput with { Name = "Revised Raven" }, issued[0].Profile.ProfileId));
            var restartedKeepsakes = new KeepsakeStore(config, clock, protection);
            var restartedSource = new DuelIdentitySource(restartedKeepsakes, memory);
            var restartedDuels = new DuelStore(config, clock, restartedSource, slot, protection);
            var historical = restartedDuels.Read(duel.Id, publicView: true);
            Check(historical.Result == duel.Result && historical.Left.WandName == oldName
                && restartedKeepsakes.Read(issued[0].Keepsake, keeper.Token).Snapshot.OfficialName == oldName,
                "profile edits and backend reopen cannot rewrite issued wand or historical battle");
            var replay = historical.Rounds.Select(round => DuelRules.Resolve(historical.Seed!, round.Number,
                historical.Left, historical.Right, DuelRules.Commit(historical.Seed!, round.Number, historical.RulesVersion),
                round.LeftAction, round.RightAction, historical.RulesVersion)).ToArray();
            Check(replay.SequenceEqual(historical.Rounds) && DuelRules.Result(replay, false) == historical.Result,
                "persisted snapshot/seed/rules reproduce the complete battle");
            restartedKeepsakes.Delete(issued[0].Keepsake,
                KeepsakeActor.ForAccount("alice", "account-session-alice", () => true), KeepsakeDeleteScope.All, "flow-delete");
            Check(wands.Read("alice", "selection-alice") is null
                && restartedKeepsakes.GetPublicWizardIdentity(issued[0].Card.IdentityId, evening, "device") is null,
                "keepsake deletion atomically revokes generated assignment and public identity");
            ExpectWand("wand_identity_revoked", () => wands.Begin("alice", "selection-alice",
                issued[0].Captured, 0b100001, true));
            try
            {
                var revoked = issued[0].Wand;
                restartedKeepsakes.Issue(new KeepsakeIssueSnapshot("revoked-outcome-retry", "device", revoked.PhysicalSlot,
                    revoked.WandId, revoked.Name, revoked.Lore, "synthetic_fixture_timing_evidence", "generated-wand-v1",
                    EveningId: evening, GeneratedWand: revoked.ToPublic()));
                throw new InvalidOperationException("Revoked assignment reissued as a fresh keepsake");
            }
            catch (KeepsakeException exception) when (exception.Code == "personal_wand_unavailable") { }
            try { restartedDuels.Read(duel.Id); throw new InvalidOperationException("Deleted history survived"); }
            catch (DuelException exception) when (exception.Status == 404) { }
            Check(wands.Read("bob", "selection-bob")?.SelectedWand?.WandId == issued[1].Wand.WandId
                && restartedKeepsakes.GetPublicWizardIdentity(issued[1].Card.IdentityId, evening, "device") is not null,
                "deletion leaves the other participant's independent wand and keepsake intact");
            Console.WriteLine("Generated wand profile → enrichment → assignment → keepsake → public identity → three-round duel → restart/history/deletion flow passed (fake providers).");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void ExpectWand(string code, Action action)
    { try { action(); throw new InvalidOperationException("Expected " + code); } catch (WandGenerationException exception) when (exception.Code == code) { } }
    private sealed class FakeWandProvider : IWandGenerationProvider
    {
        public Task<WandCandidateContent[]> GenerateAsync(WandGenerationJob job, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(WandGenerationValidation.Slots(job.Request.ReadyMask).Select(slot =>
                new WandCandidateContent($"{job.Request.Profile.UserInput.Name} — Astrální ozvěna "
                    + new[] { "Jitřní", "Modrá", "Zelená", "Stříbrná", "Rudá", "Večerní" }[slot - 1], "Fictional moonwood",
                    "Imagined constellation", "Patient", "A fiction about a lost constellation.", "A digital wand woven from star charts",
                    "Stars", "Cloudy skies", new[] { "strike", "trick", "shield" }.Select(action =>
                        new WandSpell(action, $"{job.Request.Profile.UserInput.Name} {slot} {action}", "Fictional magical stars")).ToArray())).ToArray());
        }
    }
    private sealed class Clock : TimeProvider
    { public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-10-24T16:00:00Z"); }
    private sealed class WebSlot : IDuelShowSlot
    {
        private string? current;
        internal int Rounds;
        public bool TryOccupy(string deviceId, string eveningId, string duelId)
        { if (current is not null) return current == duelId; current = duelId; return true; }
        public bool IsCurrent(string deviceId, string duelId) => current == duelId;
        public void PublishRound(string deviceId, string duelId, int round, string quip) => Rounds++;
        public void Release(string deviceId, string duelId) { if (current == duelId) current = null; }
        public DuelNarrationBinding BindNarration(string deviceId) => new("web_only", "synthetic_no_device");
        public bool EnqueueNarration(string deviceId, string duelId, int round, string scene, string wandId, string text)
            => throw new InvalidOperationException("Web-only fixture must never dispatch hardware narration");
        public string NarrationState(string deviceId, string duelId) => "idle";
    }
}
