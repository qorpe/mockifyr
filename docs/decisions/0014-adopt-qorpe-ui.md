# 0014 — The dashboard composes from the published @qorpe/ui kit

Status: Accepted (2026-08-07)

## Context

The qorpe family (Goldpath console, this dashboard, every product console to come)
shares one visual standard. Until now each app hand-kept its own components: this
dashboard's 24 files had zero tests, four coexisting selection mechanisms and a dead
`@radix-ui/react-select` dependency, while the Goldpath console kept a parallel set.
The family kit was extracted to **github.com/qorpe/ui** (`@qorpe/ui` on npm,
Apache-2.0) with tests, axe and visual gates (G1–G6), strings-as-props i18n and
RTL-as-acceptance-criterion — several of its components (Button, Switch, EmptyState,
DropdownMenu, JsonEditor) were PROMOTED from this codebase and gained their first
tests there.

## Decision

The dashboard pins the published `@qorpe/ui` (exact version, no ranges) and retires
its local copies as the kit twins cover them. The kit's `tokens.css` is the token
contract; this repo keeps only `--brand` and app-specific extras on top. Domain
visuals (method/protocol/status chips, illustrations, brand mark) STAY app-local —
adopting the kit is not a restyle. Kit API gaps discovered during migration are fed
BACK to the kit repo as issues rather than forked around.

## Consequences

- M1 (this change): Button, Switch, EmptyState, DropdownMenu (CheckItem is a real
  `menuitemcheckbox` now) swap to the kit; `index.css` drops from 159 lines to 21;
  the duplicate `StatusChip` in http-view dies in favour of `badges.StatusCode`.
- M2 (delivered): FacetFilter/SearchBox swap onto kit 0.1.1's adopter-feedback
  additions; tooltips rework onto the kit wrapper; the stub-editor and ws-detail
  sheets ride the kit Sheet (0.1.2's `maxWidth` was fed back for the wide/narrow
  panels); the CodeMirror editor moves to `@qorpe/ui/json-editor` behind a thin
  app wrapper that binds i18n once. Locals deleted: facet-filter, search-box,
  tooltip, dropdown-menu, json-editor.
- M3 (delivered): every form select becomes the family Select — the ELEVEN
  `NativeSelect` sites across the stub editor, channel editors, the test-request
  dialog and the messages behaviors panel. react-hook-form sites go through a
  15-line `SelectField` Controller bridge (the kit's own D4 pattern); plain-state
  sites call the kit Select directly. `NativeSelect` is deleted: the OS popup that
  never matched the app's own menus is gone, and every select now carries an
  accessible name, the family listbox, the audible keyboard walk, disabled
  options and the viewport flip.
### What is still local, and WHY (audited 2026-08-09)

"By design" without a reason is how a duplicate hides. Every local component now carries
one, or a trigger that ends it:

| Local | Kit twin? | Reason it stays — or the trigger that ends it |
|---|---|---|
| `journal/journal-detail`, `pages/messages` sheets | `Sheet` | Their headers are INTERACTIVE (hover-to-copy subject lines, channel-aware metadata). The kit's title is a string. Filed as the kit's next gap: a `header` slot ([qorpe/ui#29](https://github.com/qorpe/ui/issues/29)). **Trigger:** that slot ships. |
| `ui/field` (Input/Textarea/Label) | `Field`, `Input`, `Textarea` | The kit's `Field` wires label+description+error as ONE anatomy; this app's forms label separately (`<Label>` above a grid of controls) and would need re-layout, not re-import. **Trigger:** the next form-heavy screen — it is built on the kit's `Field` and the old ones follow. |
| `ui/tabs` | `TabStrip`/`TabPanel` | Radix Tabs here host ROUTED, lazily-mounted panels (stub editor Form/JSON, message detail); the kit's strip owns its own selection state. **Trigger:** the kit's strip grows a controlled-panel story, or these screens stop routing through tabs. |
| `ui/confirm-dialog` | `Dialog` + `VerbButton` | The kit's confirm lives INSIDE `VerbButton` (confirm-before-verb); this app's destructive actions are not admin verbs over the `AdminResult` envelope, so the pattern does not fit yet. **Trigger:** the dashboard adopts the verb envelope. |
| `ui/context-menu` | — (not promoted) | The kit deliberately did NOT take it: this implementation has no keyboard support, and shipping that into the family would export a defect. Written in the extraction RFC's D3. **Trigger:** a second consumer needs right-click menus → rebuild on Radix, in the kit. |
| `layout/app-shell`, `layout/app-sidebar`, `layout/tenant-switcher`, `command-palette`, `login-gate`, `error-boundary` | `AppShell`, `PageHeader`, `CommandPalette` | **The real debt (~690 lines).** These predate the kit and were missed by M1–M3 because they are shell, not screens. The kit's `AppShell` takes nav items as props and would host this nav; the sidebar's live per-tenant count badges and the tenant switcher are mockifyr-domain and stay. **Trigger: M4** — one slice, visual-diffed against the current shell, because the shell is the one surface where a regression is felt on every screen. |
| `ui/badges`, `ui/http-view`, `ui/illustrations`, `ui/brand-mark`, `stubs/*`, `templating/*` | — | Domain visuals: HTTP methods, protocols, status ramps, product art, brand. Adopting the kit is not a restyle; these are the app's own vocabulary (RFC D7: mechanism in the kit, taxonomy app-side). Permanent. |

An audit on 2026-08-09 also found this repo still shipping a 448-line `docs/design-system.md`
that declared itself the single source of truth and told the next project to "copy the
primitives" — the exact habit the extraction ended. It is a pointer stub now, as is the
dashboard README's design-system section. The standard lives in the kit, with the component
inventory generated from its barrel.

One standard violation survived the same way: `ui/sheet.tsx` kept a literal `bg-black/40`
scrim after the kit's B1 turned that into `--overlay`. Fixed; the kit's rule ("a literal
scrim or shadow in a component is a defect") now holds here too.
- Kit gaps fed back so far: 0.1.1 `FacetFilter compact/className` +
  `SearchBox className` (shipped), 0.1.2 `Sheet maxWidth` (shipped), next: a
  Sheet custom-header slot.
- The i18n strings the kit needs arrive via its labels props from this app's
  i18next — the kit itself stays framework-free.
