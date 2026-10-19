using Kufr.Backend.Features.Character;

namespace Kufr.Backend.Features.Show;

internal static class ShowSceneLibrary
{
    internal const string Version = "show-scenes-v1";
    internal static readonly string[] Archetypes = ["coronation", "ministerial-complaint", "wand-rebellion",
        "professional-funeral", "luxury-fraud", "bad-taste-prophecy", "shelf-dispute", "approved-silence", "room-roast"];

    private static readonly string[] Directions =
    [
        "Přehnaná korunovace: slavnostně korunuj obyčejné rozhodnutí; království je fikce.",
        "Ministerská reklamace: verdikt jako absurdní razítko, žádný skutečný úřad.",
        "Vzpoura hůlky: cituj fiktivní protest rekvizity; skutečný výběr neměň.",
        "Pohřeb profesionality: pohřbi vlastní obchodní profesionalitu, nikdy hosta.",
        "Luxusní podvod na očekávání: velkolepý úvod, suchá pointa; žádná skutečná platba.",
        "Proroctví špatného vkusu: krátká absurdní fikce kufru; žádné tajné proroctví hosta.",
        "Spor mezi regály: cituj žárlivost rekvizit bez soukromých historek a změny výběru.",
        "Úředně schválené ticho: jedna strohá pointa po krátké doložené pauze.",
        "Roast místnosti: terčem je jen kufr a absurdní provoz večera. Žádné konkrétní osoby, vzhled ani domnělá opilost; žádné tvrzení, že vidíš sál."
    ];
    private static readonly string[][] Beats =
    [
        ["ceremonial-entrance", "disproportionate-honour", "royal-expectation", "dry-coronation"],
        ["application-opened", "missing-attachment", "fictional-rejection", "binding-stamp"],
        ["fictional-protest", "trunk-arbitration", "choice-unchanged", "reluctant-bow"],
        ["professional-silence", "trunk-obituary", "dignity-lowered", "business-continues"],
        ["luxury-promise", "expectations-inflated", "nothing-for-sale", "dry-disclosure"],
        ["fiction-declared", "absurd-omen", "safe-public-forecast", "trunk-consequence"],
        ["first-shelf-claim", "rival-objection", "trunk-verdict", "single-choice-remains"],
        ["silence-announced", "verified-short-pause", "silence-approved", "one-dry-line"],
        ["anonymous-trunk-target", "absurd-shop-complaint", "no-personal-target", "trunk-self-roast"]
    ];

    internal static ShowScene Create(string archetype, string generation, bool express, bool motorPermitted,
        FinaleProfile profile)
    {
        var index = Array.IndexOf(Archetypes, archetype);
        if (index < 0 || string.IsNullOrWhiteSpace(generation)) { throw new ArgumentException("Unknown scene or generation."); }
        // Named remains closed until the existing controlled-publication gate opens.
        // These plans never dispatch a motor or infer playback completion.
        var variant = !motorPermitted ? "no-motor" : express ? "express" : "normal";
        int[] normalDurations = [12000, 11000, 10000, 9000, 11000, 12000, 12000, 7000, 9000];
        int[] pauseDurations = [600, 250, 350, 900, 500, 700, 300, 1200, 400];
        var duration = express ? 6000 : normalDurations[index];
        var pause = express ? Math.Min(250, pauseDurations[index]) : pauseDurations[index];
        var prefix = $"{archetype}/{variant}";
        var instruction = Directions[index] + (express ? " Jedna krátká pointa." : " Stručný úvod a jedna pointa.")
            + " Bez názvu/slotu a tvrzení pohybu; současná audio brána platí.";
        var cues = new List<ShowCue>
        {
            new(prefix + "/direction", "ActorDirection", "selection_validated", 0, duration, "drop", generation, instruction),
            new(prefix + "/pause", "Pause", "playback_finished", 0, pause, "drop", generation, pause.ToString()),
            new(prefix + "/beat", "UiBeat", "scene_started", 0, duration, "drop", generation, archetype)
        };
        cues.Add(new(prefix + "/light", "LedPreset", "scene_started", 0, 2000, "drop", generation, "consider"));
        cues.Add(new(prefix + "/local-ending", "PlayLocalClip", "playback_finished", 0, Math.Min(8000, duration),
            "drop", generation, express ? "ending-2" : "ending-1"));
        if (motorPermitted && profile == FinaleProfile.Limited)
        {
            cues.Add(new(prefix + "/search-pulse", "VibratePreset", "selection_validated", 0, 2000,
                "drop", generation, "presentation-search-pulse"));
            cues.Add(new(prefix + "/presentation", "PresentSelected", "existing_proposal_committed", 0, 30000,
                "cancel", generation, "existing-local-selection-owner"));
        }
        var beats = express ? new[] { Beats[index][0], Beats[index][^1] } : Beats[index];
        for (var beat = 0; beat < beats.Length; beat++)
        {
            // These are local semantic boundaries, not an estimate of when a
            // generated word will arrive or a simulated playback completion.
            cues.Add(new(prefix + "/stage/" + beat, "UiBeat", beat == 0 ? "scene_started" : "local_beat_advanced",
                0, duration, "drop", generation, beats[beat]));
        }
        if (motorPermitted)
        {
            cues.Add(new(prefix + "/handoff", "WaitForEvent", "existing_presentation_outcome", 0, 30000,
                "cancel", generation, "existing-local-selection-owner"));
        }
        else
        {
            cues.Add(new(prefix + "/limited", "UiBeat", "scene_started", 0, duration, "drop", generation,
                "Bez motoru; fyzická prezentace nepotvrzena."));
        }
        var scene = new ShowScene(archetype, 1, variant, ["ready", "closing"],
            ["actor-direction", "optional-led-presets", "optional-local-audio"], 30,
            motorPermitted ? 30000 : duration, [profile.ToString().ToLowerInvariant()],
            ["current-session-generation", "current-selection-revision", "existing-audio-gate"],
            "cancel-optional-cues-and-release-audio; local-stop-remains-device-owned", "no-motor", cues.ToArray());
        Validate(scene);
        return scene;
    }

    internal static ShowScene[] Definitions(string generation) => Archetypes.SelectMany(id => new[]
    {
        Create(id, generation, false, true, FinaleProfile.Blocked),
        Create(id, generation, true, true, FinaleProfile.Blocked),
        Create(id, generation, false, false, FinaleProfile.Blocked)
    }).ToArray();

    internal static string Select(IReadOnlyList<string> recent, string generation)
    {
        var candidates = Archetypes.Where(id => !recent.TakeLast(3).Contains(id)).ToArray();
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(generation));
        return candidates[hash[0] % candidates.Length];
    }

    internal static void Validate(ShowScene scene)
    {
        var kinds = new[] { "ActorDirection", "PlayLocalClip", "LedPreset", "VibratePreset", "PresentSelected", "WaitForEvent", "Pause", "UiBeat" };
        if (!Archetypes.Contains(scene.SceneId) || scene.Version != 1 || scene.MaximumDurationMs is <= 0 or > 30000
            || scene.Cues.Length == 0 || scene.Cues.Select(c => c.CueId).Distinct().Count() != scene.Cues.Length
            || scene.Cues.Select(c => c.SessionGeneration).Distinct().Count() != 1
            || scene.Phases.Length == 0 || scene.Phases.Any(p => p is not ("ready" or "closing"))
            || scene.AllowedFinaleProfiles.Length == 0 || scene.AllowedFinaleProfiles.Any(p => p is not ("named" or "limited" or "blocked"))
            || string.IsNullOrWhiteSpace(scene.Cancellation) || scene.Fallback != "no-motor"
            || !scene.Preconditions.Contains("existing-audio-gate")
            || scene.Cues.Any(c => !kinds.Contains(c.Kind) || c.OffsetMs < 0 || c.DeadlineMs <= 0
                || c.DeadlineMs > scene.MaximumDurationMs || c.LatePolicy is not ("drop" or "cancel")
                || string.IsNullOrWhiteSpace(c.SessionGeneration) || c.Value.Length > 400
                || c.TargetSlot is not null && c.TargetSlot is not (>= 1 and <= 6)
                || c.Kind == "Pause" && (!int.TryParse(c.Value, out var pause) || pause <= 0 || pause > c.DeadlineMs))
            || scene.Variant == "no-motor" && scene.Cues.Any(c => c.Kind is "PresentSelected" or "VibratePreset"))
        { throw new InvalidOperationException("Invalid show scene definition."); }
    }
}
