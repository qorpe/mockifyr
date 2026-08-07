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
- M2 (next): forms move onto the kit Select/Field (the four `NativeSelect` sites),
  sheet/tooltip/confirm rework onto kit twins.
- Kit gaps fed back for 0.1.1: `FacetFilter` needs `compact` + `className`,
  `SearchBox` needs `className`.
- The i18n strings the kit needs arrive via its labels props from this app's
  i18next — the kit itself stays framework-free.
