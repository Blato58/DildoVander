---
name: dildovander-show-authoring
description: Author or validate DV02 effect scenes, Show Pack cue plans and offline repertoire/banks, or inspect compiled scenes. Excludes ordinary persona rewriting, servo calibration, wiring, deployment and runtime trace diagnosis.
---

Locate the repository with `git rev-parse --show-toplevel`. Read the
[DV02 guide](../../../docs/dv02.md) and the relevant language/invariant
sections of [the specification](../../../docs/dv02-spec.md).

For physical choreography edit the authored [effects.json](../../../mechanika/final/effects.json).
The schema, compiler, generated header and observer plan are one source-bound
bundle. Do not copy times or colors into skills, C++ or JavaScript. Legacy main
and short are migration baselines; artistic changes require an explicit diff.
New accent entries stay disabled unless their actual owner capability is enabled.
No scene grants motion authority or changes runtime calibration.

Show Pack narrative cues use Features/Show/ShowSceneLibrary and its one Validate
entry point; offline clips use firmware/offline-bank/manifest.json and
prepare_offline_bank.py. Read [the contract](../../../docs/show-pack.md).
Optional narrative cues never dispatch actuators. Offline presentation reuses
the existing sequence/journal. Run backend `--show`, affected firmware host tests
and the bank generator `--check`. Generate headers through the actual tool;
missing approved recordings remain missing_assets. Test tones are not repertoire.
Match the configured voice and existing OTA capacity; installation is separate.
For live duel/ambient delivery inspect ShowDeviceCueBridge and the firmware
control-loop mailbox. Require exact boot/generation/revision and playback ACK;
web-only results and submitted audio never prove audible device narration.
GET audio reads a claimed result; it must not start another provider attempt.
Authored online speech uses ShowSpeechProvider and the bounded official SDK
ShowCueSpeechClient. It is separate from the two retained conversation engines;
do not restore the retired STT/dialog/TTS runtime. Run `--show-speech` offline
transport tests for changes to this adapter, plus actual bridge cancellation.

From the repository root:

```text
python mechanika/final/generate_effects.py
python mechanika/final/generate_effects.py --check
python mechanika/final/test_effects.py
python firmware/tests/run_show_tests.py
node --test mechanika/final/test_show_timeline.cjs
```

Inspect the source and generated plan diff, identities, optional audio policy,
physical limits and worst-case deadline. Run the check mode first for a read-only
audit. Rebuild the actual viewer by the guide's existing CAD generation path when
rendered output changes. Track actual evidence with
[project tracking](../dildovander-project-tracking/SKILL.md); never call an offline
trace a physical acceptance or initiate flash/paid tests through this workflow.

Trigger examples: “Change the reveal light phases”; “Validate my DV02 scene”.
Exclude: “Rewrite DV01 roast”; “Calibrate servo 3”; “Why did this operation Stop?”
