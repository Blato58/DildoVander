# Shared runtime contract

The maintained application uses Vue 3 SFCs, Composition API, TypeScript, Vite,
Vue Router and Capacitor. Import `arcane-workshop.css` once. Discover shared
Vue primitives in `mobilni-aplikace/src/components/` and domain components in
`src/features/`. See [application boundaries](../docs/application-architecture.md).
The functions below document compatibility for local service tools and archived
standalone distributions; do not use them to create new application screens.

`tokens.json` is the token source. Run `python scripts/generate_design_tokens.py`
from the repository root after editing it; `--check` checks all committed CSS,
ES module, TypeScript declaration and C# outputs. Primitive values never change
with a theme. JavaScript imports `primitives`, `themes`, `typography`, `layout`
and `motion` from `tokens.mjs`; keys omit the `--aw-` prefix. Semantic values
retain CSS `var()` aliases for browser use. Objects are recursively frozen.

The generator also checks the native launch storyboard's background RGB without
owning its XML layout, and the delimited CSS/theme block inside
`mobilni-aplikace/scripts/publish-ios-install.py`. That generated block keeps the
private script-only publisher self-contained; it derives token and component CSS
from current sources. Archived pages use the canonical system font fallbacks
offline; the existing same-origin stylesheet supplies local fonts when online.

## Markup

All renderers return HTML strings. Text and attribute values are escaped;
`escapeHtml(value)` is also available. The owning application mounts markup
and keeps state, transport, authorization and event ownership.

| Function | Options |
| --- | --- |
| `button` | `label`, `variant` (`primary`, `secondary`, `danger`, `ghost`), `size` (`normal`, `small`), `disabled`, `loading`, `type`, `id`, `attributes` |
| `statusBadge` | `label`, `tone` |
| `notice` | `title`, `message`, `tone`, optional escaped `detail`, trusted HTML `action`, `live` (default true) |
| `emptyState` | `title`, `message`, optional trusted HTML `action` |
| `wandCard` | `title`, `subtitle`, `number`, `imageUrl`, `selected`, `disabled`, `interactive` |
| `field` | `id`, `label`, trusted HTML `control`, `description`, `error` |

Tone is `neutral`, `info`, `success`, `warning` or `danger`. Notices announce
danger as an alert, other tones as status. Badges are static text; they do not
create repeated live announcements. Loading buttons are natively disabled and
expose `aria-busy`; their label remains the caller's explicit status text.
Persistent readiness rendered on every snapshot uses `live: false`; announce
meaningful transitions separately. Put the next supported action in `action`
and what remains preserved or usable in `detail`. Do not promise cancellation,
retries or physical completion that the owning feature cannot establish.
TypeScript checks the option unions. At runtime unknown variant/size/type values
fall back to secondary/normal/button, and unknown tones fall back to neutral;
raw strings never become class names or button types.

Button `attributes` accepts `class` (merged), `data-*`, `aria-*`, `name`, `value`,
`title` and `form`. Other attribute names are ignored; event handlers and styles
are never interpolated. Boolean ARIA/data values become `true`/`false` strings.
Set disabled/loading through their dedicated options. Image URLs accept local,
relative, HTTP(S) and blob sources; active/opaque schemes are excluded.
Wand cards default to native selectable buttons. `interactive: false` renders
an article with `data-selected`/`data-disabled` metadata instead, for consumers
whose actual actions live in separate controls. It never nests action buttons.

`element(markup, ownerDocument?)` mounts exactly one root element using a
template. It accepts only shared renderer output or trusted application HTML.
It is not a sanitizer. `field.control` and `emptyState.action` have the same
trust boundary: never pass visitor/provider content as raw markup. The field
renderer connects the first input/select/textarea to its visible label and
description/error; the caller still owns validation and submit behavior.

## Native behavior

`bindTabs(tablist, { onSelect(tab) }?)` returns a cleanup function. Mark buttons
with `role="tab"`, stable IDs and `aria-controls`; put `role="tabpanel"` on
their target panels. Panels may be siblings outside the tablist. The helper
owns `aria-selected`, panel visibility/label and roving tabindex. Arrow keys,
Home and End activate enabled tabs; vertical orientation uses Up/Down. Native
disabled and `aria-disabled="true"` tabs are skipped. Initialize one tab with
`aria-selected="true"`. Call cleanup before an owning rerender/rebinding.

`bindDialog(nativeDialog, { initialFocus, onClose(returnValue) }?)` returns
`open(invoker?)`, `close(returnValue?)` and `destroy()`. It uses native
`showModal()` focus containment, handles cancel/Escape and restores focus to
the connected invoker on close. `initialFocus` accepts an element or selector;
otherwise autofocus/safe cancel/first control receives focus. The caller supplies
`aria-labelledby`, descriptive text and any existing confirmation decision.
Destroy removes listeners and closes an open dialog. Use one binding per node.

## Verification

Run `npm test` and `npm run tokens:check` in this directory. Tests exercise
escaping, native disabled/loading semantics, label/error associations, tab
keyboard behavior and dialog cleanup/focus restoration with a narrow DOM fixture.
They do not establish actual browser, VoiceOver or hardware acceptance.

Run `npm run preview`, then open
`http://127.0.0.1:8765/design-system/playground/` for an interactive,
offline-safe visual check. The playground has fixed data, no transport and no
credentials; theme changes, tabs, card selection and dialog are local only.
