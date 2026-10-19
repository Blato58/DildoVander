---
name: dildovander-character-design
description: Design or change DildoVander DV01 persona, adult/chaos roast, dramaturgy, evidence anchors, lore or conversation pacing. Excludes unrelated C#, CAD, wiring and servo calibration.
---

# DildoVander character design

For Show Pack repertoire, mood, procedural finale and anonymous callbacks, read
[the integrated contract](../../../kufr/docs/show-pack.md). Extend ShowRepertoire,
ShowSceneLibrary and actual Live consumption while preserving the original
catalog. ShowCoordinator persists direction; CharacterDirector remains pure.
Personal anchors and anonymous EveningLore are separate scopes. A planned line
is not documented speech: use the audio submission proxy without claiming exact
words. Run backend `--show` and DV01 replay for changed behavior.

Read [DV01 implementation and gates](../../../kufr/docs/dv01.md) and the relevant
sections of the [character bible](../../../kufr/docs/dv01-specification.md).
Use the tracked project workflow for substantive changes. Preserve original
persona hashes, voice, model configuration and catalog identities.

Separate an authorial style change from evidence, consent, selection or physical
contracts. CharacterDirector is deterministic and has no I/O. An appearance/money joke or
profanity is not itself an integrity failure; invented hidden personal facts,
withdrawn consent, wrong wand, duplicate reveal and Stop violations are failures.
Never soften ordinary comeback or short answers automatically. Adult is the
opt-in adult preset; chaos stays within the frozen host ceiling. Clear opt-out
ends personal roast; express reduces questions independently of tone.

Edit authoritative Character/GptLive sources and synthetic fixtures. Produce
3–5 paired adult/chaos authorial examples, including low input and a correction;
show the concrete setup and payoff. They are fixtures, not generated Live proof.
Use at most three current revisioned anchors. Existing costume consent does not
authorize appearance roast. Limited finale is explicit and lacks spoken identity;
named is blocked until full controlled publication is actually supported.

Run from the repository root:

```text
python kufr/ai-eval/evaluate_party.py --dv01-replay --output kufr/ai-eval/results/dv01-offline.json
python -m unittest discover -s kufr/ai-eval -p test_dv01.py
```

Report changed behavior, source/hash identity, relevant R/T coverage, actual
measurements, regressions and open gates. Paid Live, physical tests, deployment
and new photo capture need their existing authorization. Rollback applies to new
sessions; urgent interruption uses the existing Stop/abort owner.

Trigger dry-run: “Uprav adult/chaos pointu a callback” selects this workflow;
“Zkalibruj servo” and “Přesuň CAD otvor” do not. Validate metadata/links and these
declared boundaries with the offline suite. New skill discovery requires a fresh
Codex session; do not call file existence proof of a model's routing choice.
