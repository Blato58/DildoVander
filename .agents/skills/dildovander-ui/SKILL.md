---
name: dildovander-ui
description: Implement DildoVander's shared Vue web/Capacitor application and integrated viewer with Vapour Chrome tokens; preserve native, guest, device and model-release boundaries.
---

# DildoVander UI

For Show Pack's existing Capacitor/web Director use
[the operational contract](../../../docs/show-pack.md). Distinguish backend
direction, physical state and unknown/cached results. Operator unlock does not
return another guest's revelation. Verify real API flows, two-operator conflicts,
local short/quiet/ambient and delayed socket close. The mobile
`scripts/check-director-browser.mjs` uses an isolated fake backend; verify the
served checkout/port before accepting screenshots. Package identity, duel and
chronicle Vue components through the compiled application consumed by
`web/build-site.py --web-dist`. Keep legacy guest URLs as explicit routes,
without restoring separate HTML applications.

Read the current `AGENTS.md`, applicable nested instructions and component
README first. Then read `design-system/README.md` and the relevant
`style-guide.md`, `components.md`, `accessibility.md`, `screens.md`. Paths are
repository-relative; locate the repository from this tracked skill rather
than assuming a checkout or runtime. Verify manifests, locks, routes, state
owners, styling and scripts in this branch before choosing checks.
For navigation, asynchronous work and recovery use the implemented journey
contract in `design-system/journeys.md`; preserve its feature ownership
and truth boundaries rather than creating a parallel workflow in each surface.

Vapour Chrome is authoritative for new and modified UI. The existing filename
`design-system/arcane-workshop.css` remains the shared entry point. Reuse
canonical `--aw-*` tokens and `aw-*` components; add local layout styles only
where the owning view needs them. Fonts are self-hosted with OFL licenses.
Keep offline generated viewers self-contained using their real generation
path; never patch a generated viewer or ZIP as the only source change.

## Shared framework

Before creating a component inspect `mobilni-aplikace/src/components/`,
the owning feature and `docs/application-architecture.md`. Application
screens use Vue SFCs with Composition API and lazy Vue Router routes inside
the shared shell. Use typed props, slots, events and composables. Keep local
state local; Pinia is for shared application preferences/context, never a
duplicate authoritative device cache. Keep GPU objects outside deep reactivity.
Do not add HTML-string screens, whole-screen `v-html`, iframes or a second
online application. `design-system/ui.js`, `ui.d.ts` and `playground/` remain
compatibility references for local service tools and immutable old distributions;
do not copy their markup, escaping, tab handling or dialog focus management.
Read `framework.md` for API changes and `architecture.md` for platform/package
ownership. The Capacitor app uses the same web UI; Swift owns native adapters.
State, authorization and device availability remain in their feature owners.

Edit `tokens.json`, then run `python scripts/generate_design_tokens.py`.
Never hand-edit generated CSS, ESM/types or C# token constants. Local CSS
handles layout. Extend shared primitives for repeated appearance or behavior.
The same generator owns only native launch RGB and the delimited standalone
iOS installer CSS block; keep its script-only packaging and offline fallback.
For offline viewer work compose the same maintained Vue viewer and a selected
ModelBundle using the offline packager. Use labelled fixtures for UI checks;
never regenerate CAD just for chrome or require manufacturing archives for
application builds. Validate schema, paths, size, checksums and compatibility;
model source/version and app source/version are independent.

For model-only iteration use `scripts/cad.ps1 build` and `publish` (preview).
See [CAD workflow](../../../mechanika/final/CAD-WORKFLOW.md). App/viewer
checks never invoke this generator. Only the dedicated manual generation
workflow may build CAD in Actions. `verify` checks bundle integrity/schema;
`verify --manufacturing` explicitly requests the expensive existing pipeline.
Never promote an unverified preview to production or infer physical acceptance.

Read `references/application.md` for feature ownership, identity/pairing,
claim capture, viewer lifecycle and focused validation. Route guards are UX;
server permission checks, native PoP/pinning and guest capabilities own access.

After shared runtime changes run `npm test --prefix design-system`,
the token drift check and affected consumer tests. Use `test:browser` for
repeatable theme/layout/focus regression checks; its pinned browser dependency
is development-only. Include relevant private/public synthetic flows for DV10.
UI-only CI must retain viewer tests without selecting manufacturing work.

## Character and workflow

Build one cohesive Vapour Chrome across mobile, simulator, CAD viewer, public
pickup and service tools. Canonical primitives are Vapour Mist #EEF1F5,
Chrome Midnight #1B2430, Steel Grey #8A94A6 and Digital Periwinkle #B8C0FF.
Use semantic roles, Inter UI/headings and IBM Plex Mono for technical values.
Steel and periwinkle need accessible derived text shades on light surfaces.
Keep theatrical personality in content and artwork; interface chrome stays
precise, calm and useful. Keep action names, errors, Stop and safety literal.

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

## Discovery and integrity

Ordinary requests to improve UI, navigation, a mobile layout, viewer, website or
dashboard should select this project skill without requiring a named invocation.
Use the installed design plugins, never duplicate or modify their packages.
`PRODUCT.md` records product facts; `DESIGN.md` points to the canonical system.
Run `python scripts/check_design_system.py` after shared UI/guidance changes.
Weekly maintenance reads `.codex/environment/README.md`: report token, theme,
accessibility or design-tool drift; repair only guidance/config defects in its
existing scope. It must not redesign applications or regenerate CAD automatically.
