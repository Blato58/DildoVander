using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Show;

internal sealed record DuelNarrationDispatch(string DeviceId, string DuelId, int Revision, string Stage,
    int Round, string Scene, string WandId, string Text);

internal sealed partial class DuelStore
{
    private bool ProcessNarrationState(SqliteConnection connection, SqliteTransaction transaction, DuelDocument duel,
        List<DuelNarrationDispatch> dispatches, List<(string Device, string Id)> released)
    {
        if (duel.NarrationMode != "physical_device" || duel.NarrationStage == "choice") { return false; }
        if (duel.NarrationStage is "intro_dispatch" or "round_dispatch" or "final_dispatch")
        {
            dispatches.Add(CaptureNarration(duel));
            return true;
        }
        var state = slot.NarrationState(duel.DeviceId, duel.Id);
        if (state == "failed" || duel.NarrationExpiresAt is null || clock.GetUtcNow() >= duel.NarrationExpiresAt
            || duel.NarrationStage is not ("intro_wait" or "round_wait" or "final_wait"))
        {
            Interrupt(duel);
            Save(connection, transaction, duel);
            released.Add((duel.DeviceId, duel.Id));
            return true;
        }
        if (state != "idle") { return true; }
        duel.NarrationExpiresAt = null;
        if (duel.NarrationStage == "final_wait")
        {
            duel.NarrationStage = "playback_confirmed";
            CompleteDuel(connection, transaction, duel);
            released.Add((duel.DeviceId, duel.Id));
        }
        else if (duel.NarrationStage == "round_wait" && duel.Rounds.Count == 3)
        {
            duel.NarrationStage = "final_dispatch";
            Save(connection, transaction, duel);
            dispatches.Add(CaptureNarration(duel));
        }
        else
        {
            duel.NarrationStage = "choice";
            // Phone timeouts begin after the preceding narration has actually
            // drained. A slow speaker never consumes the guest's choice window.
            duel.Deadline = clock.GetUtcNow().AddSeconds(duel.Mode == "interactive" ? 15 : 3);
            Save(connection, transaction, duel);
        }
        return true;
    }

    private void DispatchNarration(DuelNarrationDispatch dispatch)
    {
        // The bridge persists in this same database, so enqueue outside our
        // transaction. It independently fences occupancy/cancellation generation.
        var accepted = slot.EnqueueNarration(dispatch.DeviceId, dispatch.DuelId, dispatch.Round,
            dispatch.Scene, dispatch.WandId, dispatch.Text);
        var release = false;
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var duel = Load(connection, transaction, dispatch.DuelId);
            if (duel.State != "Running" || duel.Revision != dispatch.Revision || duel.NarrationStage != dispatch.Stage) { return; }
            if (accepted)
            {
                duel.NarrationStage = dispatch.Stage.Replace("_dispatch", "_wait", StringComparison.Ordinal);
                duel.NarrationExpiresAt = clock.GetUtcNow().AddSeconds(25);
            }
            else
            {
                Interrupt(duel);
                release = true;
            }
            Save(connection, transaction, duel);
            transaction.Commit();
        }
        if (release) { slot.Release(dispatch.DeviceId, dispatch.DuelId); }
    }

    private void CompleteDuel(SqliteConnection connection, SqliteTransaction transaction, DuelDocument duel)
    {
        duel.State = "Completed";
        duel.Deadline = null;
        duel.NarrationExpiresAt = null;
        Save(connection, transaction, duel);
        Execute(connection, transaction, "INSERT OR IGNORE INTO show_duel_events(duel_id,document) VALUES($id,$document)",
            ("$id", duel.Id), ("$document", Serialize(new DuelCompletionEvent(duel.Id, duel.DeviceId,
                duel.EveningId, duel.Revision, duel.Mode == "exhibition"))));
        if (commentaryEnabled)
        {
            Execute(connection, transaction, "INSERT OR IGNORE INTO show_duel_commentary(duel_id,state) VALUES($id,'pending')", ("$id", duel.Id));
        }
    }

    private static void Interrupt(DuelDocument duel)
    {
        duel.State = "Interrupted";
        duel.NarrationStage = "interrupted";
        duel.Deadline = duel.NarrationExpiresAt = null;
        duel.LeftChoice = duel.RightChoice = null;
        duel.Result = null;
    }

    private static DuelNarrationDispatch CaptureNarration(DuelDocument duel)
    {
        var stage = duel.NarrationStage;
        var round = stage == "intro_dispatch" ? 0 : duel.Rounds.Count;
        var scene = stage switch { "intro_dispatch" => "wake", "final_dispatch" => "farewell", _ => "consider" };
        var wandId = stage == "round_dispatch" && round % 2 == 0 ? duel.Right.WandId : duel.Left.WandId;
        var line = stage switch
        {
            "intro_dispatch" => $"{(duel.Mode == "exhibition" ? "Fiktivní exhibice. " : "")}{BoundAlias(duel.Left.DisplayName)} proti {BoundAlias(duel.Right.DisplayName)}. Digitální souboj začíná. Hůlky zůstávají v přihrádkách.",
            "final_dispatch" => $"{(duel.Mode == "exhibition" ? "Fiktivní exhibice se nezapočítává jako výhra hosta. " : "")}{duel.Result!.Ending} Body {duel.Result.LeftPoints} ku {duel.Result.RightPoints}.",
            _ => $"Kolo {round}. {duel.Rounds[^1].Quip} Skóre kola {duel.Rounds[^1].LeftScore} ku {duel.Rounds[^1].RightScore}. {duel.Rounds[^1].Mishap}"
        };
        return new(duel.DeviceId, duel.Id, duel.Revision, stage, round, scene, wandId, line);
    }

    private static string BoundAlias(string alias) => new(alias.Where(character => !char.IsControl(character)).Take(100).ToArray());
}
