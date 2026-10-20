namespace Kufr.Backend.Features.Show;

internal sealed partial class ShowCoordinator
{
    private Func<string, string, ShowCue, string>? enqueueSceneCue;
    private Func<string, string, string, string?>? readSceneCueStatus;

    internal bool IsCurrentShowVisit(string deviceId, string generation)
    {
        lock (gate)
        {
            var runtime = Get(deviceId);
            return runtime.State.SessionGeneration == generation
                && runtime.State.Phase is not ("stopped" or "interrupted");
        }
    }

    internal bool CanPublishCharacterFinale(string deviceId, string generation)
    {
        lock (gate)
        {
            var runtime = Get(deviceId);
            return runtime.State.Phase is not ("stopped" or "interrupted")
                && runtime.SceneOwnerGeneration == generation && runtime.FinaleSources.ContainsKey(generation)
                && runtime.Scene is { IsCancelled: false };
        }
    }

    internal bool TryCommitCharacterFinale(string deviceId, string generation, Func<bool> commit)
    {
        // Linearize a validated journal publication with operator Stop. The
        // callback commits technical journal identity only, never actuators.
        lock (gate)
        {
            return CanPublishCharacterFinale(deviceId, generation) && commit();
        }
    }

    internal void ConfigureSceneCueAdapter(Func<string, string, ShowCue, string> enqueue,
        Func<string, string, string, string?> readStatus)
    {
        enqueueSceneCue = enqueue;
        readSceneCueStatus = readStatus;
    }

    // Network delivery may acquire its own mailbox lock and read this owner.
    // Never invoke it under the coordinator lock; status reads are lock-free.
    internal void DispatchSceneCues(string deviceId, string generation)
    {
        ShowCue[] cues;
        ShowSceneExecution? execution;
        lock (gate)
        {
            var runtime = Get(deviceId);
            if (runtime.State.SessionGeneration != generation || runtime.Scene is not { } scene) { return; }
            execution = scene;
            cues = scene.TakeAdapterCues();
        }
        foreach (var cue in cues)
        {
            var result = enqueueSceneCue?.Invoke(deviceId, generation, cue) ?? "unsupported_capability";
            lock (gate)
            {
                var runtime = Get(deviceId);
                if (runtime.State.SessionGeneration != generation || runtime.Scene != execution) { continue; }
                execution.ApplyAdapterResult(cue.CueId, result);
                ProjectScene(runtime, execution.Tick());
            }
        }
    }
}
