---
name: dildovander-ui
description: Apply the shared Arcane Workshop design system to DildoVander mobile UI, simulator, Workshop viewer, and public project overview while preserving device contracts and generated artifact provenance.
---

# DildoVander UI

Read the repository's `kufr/design-system/README.md` and the relevant
`style-guide.md`, `components.md`, `accessibility.md`, `screens.md` before UI
work. Paths are repository-relative; locate the repository from this tracked
skill rather than assuming a particular checkout path.

Use `kufr/design-system/arcane-workshop.css` as the shared entry point. Reuse
canonical `--aw-*` tokens and `aw-*` components; add local layout styles only
where the owning view needs them. Fonts are self-hosted with OFL licenses.
Keep offline generated viewers self-contained using their real generation
path; never patch a generated viewer or ZIP as the only source change.

Preserve real routes, DOM hooks, accessibility, state transitions, device
authorization, lease and safety boundaries. Visual availability is derived
from the existing model. Never turn a mockup into a hardware action or label
simulated/unknown data as physically verified. Simulation and static previews
retain a visible label; published observer pages cannot accept local tokens.

Validate the actual consumer, responsive layout, keyboard focus, state text
and reduced motion. Check font/CSS delivery under its real base path and CSP.
Regenerate and inspect viewer artifacts when their source changes; web checks
do not establish iPhone or hardware acceptance. Report actual checks and gaps.
