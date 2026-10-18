---
name: dildovander-ui
description: Apply the shared Arcane Workshop design system to DildoVander mobile UI, simulator, Workshop viewer, and public project overview while preserving device contracts and generated artifact provenance.
---

# DildoVander UI

Read the current `AGENTS.md`, applicable nested instructions and component
README first. Then read `kufr/design-system/README.md` and the relevant
`style-guide.md`, `components.md`, `accessibility.md`, `screens.md`. Paths are
repository-relative; locate the repository from this tracked skill rather
than assuming a checkout or runtime. Verify manifests, locks, routes, state
owners, styling and scripts in this branch before choosing checks.

Use `kufr/design-system/arcane-workshop.css` as the shared entry point. Reuse
canonical `--aw-*` tokens and `aw-*` components; add local layout styles only
where the owning view needs them. Fonts are self-hosted with OFL licenses.
Keep offline generated viewers self-contained using their real generation
path; never patch a generated viewer or ZIP as the only source change.

## Character and workflow

Build one cohesive Arcane Workshop across mobile, Simulation Lab, CAD Workshop
and the project overview: theatrical craft, original magic and restrained
humor, grounded in the existing 80/15/5 guidance. Use the actual workshop,
suitcase, wands and mechanical ritual as context. Make hierarchy, composition
and useful interaction carry that identity; do not add decorative clutter
over telemetry. Keep action names, errors, Stop and safety language literal.

For a substantive UI change, complete this loop; scale each step to the task:

1. **Understand:** identify the user's job, existing flow, source of state,
   constraints and acceptance. Inspect current rendered behavior when available.
2. **Explore:** consider meaningful layout/interaction options against the
   canonical examples and real content. Do not invent features to fill a screen.
3. **Design:** choose hierarchy, responsive behavior, copy and states before
   editing. Cover loading, empty, error, offline/unknown and disabled states
   only where the real consumer supports them.
4. **Implement:** reuse shared `--aw-*` tokens, `aw-*` components and existing
   state/rendering ownership. Preserve routes, hooks, contracts and safety gates.
5. **Render:** run the actual consumer or generate the real viewer using its
   owning script. A static mockup is evidence only for that mockup.
6. **Inspect:** inspect screenshots and exercise the affected flow, keyboard,
   narrow/wide layouts, state text and reduced motion. Check console/network
   errors and CSS/font delivery under the real base path and CSP.
7. **Refine:** fix hierarchy, interaction and accessibility findings; render and
   recheck the changed states until the acceptance is met.
8. **Validate:** run the smallest relevant scripts and report actual rendered
   checks, generated provenance and remaining device/runtime gaps.

Use installed `impeccable:impeccable` for visual direction and polish;
`product-design:index` and its focused skill for intentional product flows,
ideation, reference fidelity or evidence-based audit. `frontend-quality-workflow`
owns implementation discipline; `browser-validation` owns rendered behavior
verification. Read a selected skill before applying it. Choose the useful
workflow for the task; a small copy/style fix does not require all of them.
Prefer the visible in-app Browser for inspection, then an authenticated browser
or Computer Use; use Playwright when repeatable interaction needs it and state why.

## Acceptance boundaries

Preserve real routes, DOM hooks, accessibility, state transitions, device
authorization, lease and safety boundaries. Visual availability is derived
from the existing model. Never turn a mockup into a hardware action or label
simulated/unknown data as physically verified. Simulation and static previews
retain a visible label; published observer pages cannot accept local tokens.

Read [targeted acceptance](references/acceptance.md) on demand for CAD/viewer
or simulator changes. It anchors checks to source without promising unimplemented
controls or physical behavior. Shared CSS and effect data do not imply a shared
rendering engine. Follow the actual consumers and test each affected one.
Web checks do not establish signed iPhone, firmware-on-board or physical acceptance.
