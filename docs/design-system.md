# Design system — moved to the kit

The Mockifyr dashboard no longer owns a design language. It composes the qorpe family kit,
so the standard lives with the package that enforces it:

- **The standard:** [`docs/ui-standard.md` in qorpe/ui](https://github.com/qorpe/ui/blob/main/docs/ui-standard.md)
  — tokens, typography, interaction rules, the status ramp, i18n/RTL, the animation rule.
- **The component inventory:** [`docs/inventory.md`](https://github.com/qorpe/ui/blob/main/docs/inventory.md)
  — generated from the package barrel, so it cannot drift.
- **What this repo decided and why:** [ADR 0014](decisions/0014-adopt-qorpe-ui.md).

The 448-line copy that used to live here was written before the extraction (2026-07-09). It
described tokens this repo no longer defines, recipes for components that are now the kit's,
and — worst — it told the next project to **copy the primitives**, which is precisely the
habit the extraction exists to end. Keeping it would have been keeping a second source of
truth that nothing updates: the failure mode a shared kit is FOR.

Re-skinning is still a one-file change: override `--primary` (kit) or `--brand` (ours) in
`ui/src/index.css`.
