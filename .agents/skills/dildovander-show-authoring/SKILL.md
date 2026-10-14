---
name: dildovander-show-authoring
description: Author or validate DV02 effect scenes, migrate presentation choreography, or inspect compiled scene changes in DildoVander. Excludes persona/dialogue writing, servo calibration, wiring, deployment and runtime trace diagnosis.
---

Locate the repository with `git rev-parse --show-toplevel`. Read the
[DV02 guide](../../../kufr/docs/dv02.md) and the relevant language/invariant
sections of [the specification](../../../kufr/docs/dv02-spec.md).

Edit only the authored [effects.json](../../../kufr/mechanika/final/effects.json).
The schema, compiler, generated header and observer plan are one source-bound
bundle. Do not copy times or colors into skills, C++ or JavaScript. Legacy main
and short are migration baselines; artistic changes require an explicit diff.
New accent entries stay disabled unless their actual owner capability is enabled.
No scene grants motion authority or changes runtime calibration.

From the repository root:

```text
python kufr/mechanika/final/generate_effects.py
python kufr/mechanika/final/generate_effects.py --check
python kufr/mechanika/final/test_effects.py
python kufr/firmware/tests/run_show_tests.py
node --test kufr/mechanika/final/test_show_timeline.cjs
```

Inspect the source and generated plan diff, identities, optional audio policy,
physical limits and worst-case deadline. Run the check mode first for a read-only
audit. Rebuild the actual viewer by the guide's existing CAD generation path when
rendered output changes. Track actual evidence with
[project tracking](../dildovander-project-tracking/SKILL.md); never call an offline
trace a physical acceptance or initiate flash/paid tests through this workflow.

Trigger examples: “Change the reveal light phases”; “Validate my DV02 scene”.
Exclude: “Rewrite DV01 roast”; “Calibrate servo 3”; “Why did this operation Stop?”
