---
name: dildovander-show-debug
description: Diagnose DildoVander DV02 show traces, gaps, timing, duplicate operations, Stop or replay differences using the existing core and read-only timeline. Excludes authoring new choreography, ordinary voice/persona evaluation, firmware flashing and paid model tests.
---

Read [DV02 diagnostics](../../../docs/dv02.md), then only the affected
clock/ownership/restart sections of [the spec](../../../docs/dv02-spec.md).
Start with the operation/boot identity, scene/bundle/compiler identity, runtime
profile, calibration revision and terminal evidence. Compare only compatible
clock domains. Enqueue, DMA transfer and sound are distinct evidence strengths.

The existing authenticated device HTTPS `GET /v1/show-trace` is read-only and
does not acquire a manual lease. Use the provisioned TLS pin and token handling
already used by local control; never put credentials in chat, shell history or
artifacts. An older device may lack this endpoint. Missing trace is unknown.

For an offline reproduction from the repository root:

```text
python mechanika/final/generate_effects.py --check
python firmware/tests/run_show_tests.py
node --test mechanika/final/test_show_timeline.cjs
```

To retain importable files on Windows, run
`python firmware/tests/run_show_tests.py --only show_trace_replay --fixtures "$env:TEMP/dv02-replay"`.
On other hosts pass a local temporary directory to `--fixtures`. This exports
synthetic traces from the actual presentation core. Open the
existing generated Workshop viewer's DV02 panel and import the JSON there. Seek,
playback speed and baseline/candidate comparison only project recorded events;
they never retry commands. Preserve gaps, missing times, quality reduction and
inspection requirements in the conclusion. Trace expiry cannot erase durable
journal uncertainty. Do not resume, lower or issue a new operation ID to resolve
an unknown physical result.

Report the exact replay inputs, command/result and unverified hardware/audio
limits. Use [project tracking](../dildovander-project-tracking/SKILL.md) for actual
evidence. Authoring changes use [show authoring](../dildovander-show-authoring/SKILL.md).

Trigger examples: “Compare these DV02 traces”; “Explain this dropped cue”.
Exclude: “Design a new reveal”; “Tune DV01 persona”; “Publish firmware”.
