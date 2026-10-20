using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Immutable;
using Kufr.Backend.Features.Character;
using Kufr.Backend.Features.Conversations;

namespace Kufr.Backend.Features.Show;

internal sealed partial class ShowCoordinator
{
    private void InitializeFinalePlans()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS show_finale_plans(device_id TEXT NOT NULL,operation_id TEXT NOT NULL,
              fingerprint TEXT NOT NULL,created_at TEXT NOT NULL,body TEXT NOT NULL,
              PRIMARY KEY(device_id,operation_id));
            """;
        command.ExecuteNonQuery();
    }

    internal void CaptureFinaleAnchors(string deviceId, string generation, IReadOnlyList<FinaleAnchorReference> anchors)
    {
        if (anchors.Count > 3 || anchors.Any(a => !ValidFinaleId(a.AnchorId) || !ValidFinaleId(a.SourceId)
                || a.SourceRevision < 0 || a.EvidenceRevision < 0 || a.PermissionRevision < 0))
        { throw new ArgumentException("Invalid approved finale anchor references."); }
        lock (gate)
        {
            var runtime = Get(deviceId);
            if (runtime.State.SessionGeneration != generation) { return; }
            runtime.ApprovedFinaleAnchors = anchors.ToArray();
            if (runtime.FinaleSources.TryGetValue(generation, out var source))
            { runtime.FinaleSources[generation] = source with { Anchors = anchors.ToArray() }; }
        }
    }

    // This is called only by the existing completion owner, with the exact
    // SelectionProposal that it has durably accepted, never an operator DTO.
    internal FinaleBindingResult BindFinaleProposal(string deviceId, string operationId, SelectionProposal proposal,
        string? generation, long selectionRevision, string personaVersion, FinaleProfile? profile, string? eveningId)
    {
        lock (gate)
        {
            if (!IsKnownDevice(deviceId) || !ValidFinaleId(operationId) || !ValidFinaleId(proposal.ProposalId)
                || !ValidFinaleId(proposal.ConversationId) || !ValidFinaleId(personaVersion)
                || generation is not null && !ValidFinaleId(generation) || selectionRevision < 0
                || proposal.Compartment is < 1 or > 6 || proposal.CatalogVersion != PartyWandCatalog.Version
                || proposal.DeviceRevision < 0 || proposal.Generation < 0)
            { return new("rejected", "invalid_finale_snapshot", null); }
            if (profile is FinaleProfile.Blocked or FinaleProfile.Named)
            { return new("rejected", "finale_profile_not_publishable", null); }
            var runtime = Get(deviceId);
            FinaleSource? source = null;
            if (profile is not null)
            {
                if (generation is not null && !runtime.FinaleSources.TryGetValue(generation, out source)
                    && ReadFinalePlan(deviceId, operationId) is { } saved)
                {
                    source = new(saved.SessionId, saved.EveningId ?? "no-active-evening", saved.Archetype,
                        saved.CuePlan.Variant == "express", profile.Value, saved.SelectionRevision,
                        saved.ApprovedAnchorReferences.ToArray());
                }
                if (generation is null || source is null
                    || source.SessionId != proposal.ConversationId || source.SelectionRevision > selectionRevision
                    || source.Profile != profile || source.EveningId != eveningId)
                { return new("rejected", "stale_finale_source", null); }
            }
            var archetype = source?.Archetype ?? "approved-silence";
            var cueGeneration = generation ?? "conversation/" + proposal.ConversationId;
            var scene = ShowSceneLibrary.Create(archetype, cueGeneration, source?.Express ?? false,
                source is not null, profile ?? FinaleProfile.Limited);
            scene = scene with { Cues = scene.Cues.Select(cue => cue with { TargetSlot = proposal.Compartment }).ToArray() };
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId + "\n" + operationId + "\n" + proposal.ProposalId)));
            var wand = PartyWandCatalog.Available(63).Single(w => w.Compartment == proposal.Compartment);
            var plan = new FinalePlan(1, "finale-" + hash, deviceId, eveningId, proposal.ProposalId, operationId,
                proposal.ConversationId, generation, proposal.Compartment, wand.StableId, proposal.CatalogVersion,
                proposal.DeviceRevision, proposal.BootId, proposal.Generation, proposal.TurnId,
                selectionRevision, ShowSceneLibrary.Version, personaVersion,
                profile?.ToString().ToLowerInvariant() ?? "existing-provider", archetype,
                (source?.Anchors ?? []).ToImmutableArray(), proposal.ExpiresAt, "no-motor", FinaleSceneSnapshot.Capture(scene));
            var body = JsonSerializer.Serialize(plan, Json);
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using (var existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = "SELECT fingerprint,body FROM show_finale_plans WHERE device_id=$d AND operation_id=$o";
                existing.Parameters.AddWithValue("$d", deviceId);
                existing.Parameters.AddWithValue("$o", operationId);
                using var reader = existing.ExecuteReader();
                if (reader.Read())
                {
                    return reader.GetString(0) == fingerprint
                        ? new("replayed", "finale_already_bound", JsonSerializer.Deserialize<FinalePlan>(reader.GetString(1), Json))
                        : new("conflict", "immutable_finale_conflict", null);
                }
            }
            if (proposal.ExpiresAt <= clock.GetUtcNow()) { return new("rejected", "finale_deadline_expired", null); }
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            // These are bounded technical snapshots; the accepted guest archive
            // and durable presentation/outcome outbox retain their own policies.
            insert.CommandText = """
                DELETE FROM show_finale_plans WHERE device_id=$d AND created_at < $cutoff;
                DELETE FROM show_finale_plans WHERE device_id=$d AND operation_id IN
                  (SELECT operation_id FROM show_finale_plans WHERE device_id=$d ORDER BY created_at DESC,rowid DESC LIMIT -1 OFFSET 127);
                INSERT INTO show_finale_plans VALUES($d,$o,$f,$now,$body);
                """;
            insert.Parameters.AddWithValue("$d", deviceId);
            insert.Parameters.AddWithValue("$o", operationId);
            insert.Parameters.AddWithValue("$f", fingerprint);
            insert.Parameters.AddWithValue("$now", clock.GetUtcNow().ToString("O"));
            insert.Parameters.AddWithValue("$cutoff", clock.GetUtcNow().AddHours(-24).ToString("O"));
            insert.Parameters.AddWithValue("$body", body);
            insert.ExecuteNonQuery();
            transaction.Commit();
            if (runtime.SceneOwnerGeneration == generation && source is not null && runtime.Scene is { } execution)
            { ProjectScene(runtime, execution.PresentationProposalBound(operationId, proposal.ProposalId)); }
            if (runtime.State.SessionId is null || runtime.State.SessionId == proposal.ConversationId)
            { Update(runtime, runtime.State with { FinalePlan = plan }); }
            return new("bound", "finale_bound", plan);
        }
    }

    internal FinalePlan? ReadFinalePlan(string deviceId, string operationId)
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT body FROM show_finale_plans WHERE device_id=$d AND operation_id=$o";
            command.Parameters.AddWithValue("$d", deviceId);
            command.Parameters.AddWithValue("$o", operationId);
            return command.ExecuteScalar() is string body ? JsonSerializer.Deserialize<FinalePlan>(body, Json) : null;
        }
    }

    private FinalePlan? ReadLatestFinalePlan(string deviceId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM show_finale_plans WHERE device_id=$d ORDER BY created_at DESC,rowid DESC LIMIT 1";
        command.Parameters.AddWithValue("$d", deviceId);
        return command.ExecuteScalar() is string body ? JsonSerializer.Deserialize<FinalePlan>(body, Json) : null;
    }

    // Call only after the existing authenticated physical-outcome path validates
    // device, proposal, boot/generation/revision and its timing evidence.
    internal bool ObserveFinaleOutcome(string deviceId, string operationId, string proposalId,
        string outcomeId, string evidence)
    {
        lock (gate)
        {
            if (!ValidFinaleId(outcomeId) || evidence != "completed_by_timing") { return false; }
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT body FROM show_finale_plans WHERE device_id=$d AND operation_id=$o";
            command.Parameters.AddWithValue("$d", deviceId);
            command.Parameters.AddWithValue("$o", operationId);
            if (command.ExecuteScalar() is not string body) { return false; }
            var plan = JsonSerializer.Deserialize<FinalePlan>(body, Json)!;
            if (plan.ProposalId != proposalId) { return false; }
            if (plan.Locked) { return plan.ObservedOutcomeId == outcomeId && plan.PresentationEvidence == evidence; }
            plan = plan with { Locked = true, State = "timed-completed", ObservedOutcomeId = outcomeId, PresentationEvidence = evidence };
            command.CommandText = "UPDATE show_finale_plans SET body=$body WHERE device_id=$d AND operation_id=$o";
            command.Parameters.AddWithValue("$body", JsonSerializer.Serialize(plan, Json));
            command.ExecuteNonQuery();
            transaction.Commit();
            if (runtimes.TryGetValue(deviceId, out var runtime) && runtime.State.FinalePlan?.FinaleId == plan.FinaleId)
            {
                if (runtime.SceneOwnerGeneration == plan.SessionGeneration && runtime.Scene is { } execution)
                { ProjectScene(runtime, execution.PresentationOutcomeObserved()); }
                Update(runtime, runtime.State with { FinalePlan = plan });
            }
            return true;
        }
    }

    private static bool ValidFinaleId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
    private sealed record FinaleSource(string SessionId, string EveningId, string Archetype, bool Express,
        FinaleProfile Profile, long SelectionRevision, FinaleAnchorReference[] Anchors);
}
