# Application themes

The shared design system supports two maintained visual identities:

| Identity | Identifier | Presentation |
| --- | --- | --- |
| Lord Voldemort | `lord-voldemort` | Default, opaque Codex-inspired dark surfaces, compact navigation, restrained panels and controls |
| Olivander | `olivander` | Original Vapour Chrome palette, typography, spacing and component treatment |

The supplied `codex-theme-v1` configuration defines Lord Voldemort's source
palette: accent `#606acc`, background `#0f0f11`, foreground `#e3e4e6`, added
`#69c967`, removed `#ff7e78`, skill `#c2a1ff`, local Inter and IBM Plex Mono.
Contrast 60 is descriptive source metadata, not an accessibility ratio. Links
use a lighter derived accent; primary controls retain the exact source accent.
Both identities currently use dark native/browser chrome. No native Codex
window was inspected; this is a supplied-palette and design-language adaptation.

## Selection and persistence

Use **Settings → Vzhled**, the shell selector, or the standalone tool control.
The Vue store and standalone tools share `theme.js`/`theme.mjs`; changes update
the document in place without routing, remounting, networking or reloads.
Dialogs and other body-level overlays inherit the root tokens. Viewer canvas
background/selection follows the theme while physical material colors retain
their meaning.

`dildovander.theme` in localStorage wins over the legacy `aw-theme` key.
Missing/invalid values resolve to `lord-voldemort`. Explicit legacy
`dark`, `light` and `system` choices migrate to Olivander's original default
dark appearance. The original light/system palettes remain available through
legacy CSS selectors for archived integrations, rather than introducing more
identities into the selector. Storage-denied environments retain the selection
for the current page. Other tabs on the same origin follow storage events;
there is no cross-origin or cross-device synchronization. The existing account
profile has no preference storage API, so this change adds no backend subsystem.

The generated synchronous resolver runs in the document head before CSS/Vue.
The Vite plugin hashes that exact inline script in the build CSP. The offline
Workshop and retained viewer embed the same resolver; file URLs may deny or
isolate local storage, in which case switching still works in memory.
The iOS installer embeds CSS/resolver/control code for its standalone fallback.
Capacitor uses the same web preference/runtime; safe areas and touch targets
remain shared. `Info.plist` selects dark native appearance for both identities
using [Apple's UIUserInterfaceStyle](https://developer.apple.com/documentation/bundleresources/information-property-list/uiuserinterfacestyle).
Native launch uses the default background, since browser storage
is unavailable before the WebView starts. Signed iPhone behavior remains a
separate device acceptance check.

## Sources and adding an identity

`tokens.json` is the canonical source for semantic palettes, fonts, base spacing,
radii/motion and `themeRegistry`. Each registry entry names a palette and
presentation dimensions. The generator emits CSS, frozen typed tokens and the
registry inside `theme.js`; do not hand-edit generated outputs.
`theme-runtime.js` owns preference behavior, while `theme-controls.js` is the
small standalone adapter. Vue components use the typed `theme.mjs` facade.

Shared primitives live in `components/patterns.css` and `framework.css`;
`identities.css` contains presentation variants. Application shell layout lives
in `mobilni-aplikace/src/application/application.css`; feature styles own local
layout. Simulator, servo panel, legacy viewer and generated source-reference document own their technical layout,
using the same palette and preference runtime. Model materials are not UI tokens.

To add a future identity, define its semantic palette and matching presentation
roles in `tokens.json`, register its identifier/label. The generator also updates the exported
`ThemeId` union. Optional component variants belong beside shared primitives.
Selectors populate from the registry automatically; unrelated pages need no
edits. Preserve required identities and the default fallback. Then run:

```text
python scripts/generate_design_tokens.py
npm test --prefix design-system
python scripts/check_design_system.py
```

Refresh the retained viewer through `viewer_style.snapshot_styles` after shared
styles/runtime change. This refresh copies presentation assets only; it does
not generate CAD, change model versions, or prove manufacturing acceptance.
Use the existing app and design-system browser suites with both identities.
The [local validation receipt](THEME-VALIDATION.md) records scope and limitations.

## Original-design checkpoint and recovery

Before any redesign edit, the clean repository was checkpointed at
`5f784a1d95b201cc50cacd33f4cf69655399ab45`, branch
`codex/backup-olivander-20261020`. All original tracked CSS, component sources,
fonts, assets and configuration remain recoverable from that Git object.
The immutable original token configuration is also retained in
`themes/olivander.original.json`; tests compare the preserved palettes and base
typography/layout/assets to it. Runtime Olivander uses maintained shared
components, not an archived stylesheet.

An additional full tracked-source ZIP was created outside the checkout at
`%TEMP%/dildovander-original-ui-20261020.zip`, SHA-256
`446B75977106423781D8A06C05B8DA41AAA455ED5E2E5FBF22FA9607259E1F5B`.
This local archive is supplementary; the Git checkpoint is the portable source.
There were no uncommitted changes at backup time.

For normal visual recovery, select **Olivander**; this preserves all subsequent
functional changes. To inspect the exact historical implementation separately:

```text
git worktree add --detach ../DildoVander-original-ui 5f784a1d95b201cc50cacd33f4cf69655399ab45
git diff 5f784a1d95b201cc50cacd33f4cf69655399ab45 -- design-system mobilni-aplikace/src/application
```

For selective source recovery, extract the required historical files with
`git show CHECKPOINT:path/to/file` into temporary files, review the diff, and
apply only presentation hunks. Never restore whole Vue/JS files over newer
functional changes or reset the repository to recover its appearance.
