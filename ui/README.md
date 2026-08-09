# Mockifyr Dashboard (`ui/`)

The admin dashboard for Mockifyr — a decoupled single-page app that talks **only** to the mock
server's `/__admin/*` REST API. It cannot reach the engine directly, so it can never affect
matching/serving; the .NET side is untouched by this project.

## Stack

- **React 19 + TypeScript + Vite**
- **[`@qorpe/ui`](https://www.npmjs.com/package/@qorpe/ui)** — the qorpe family kit: tokens,
  primitives and composites, pinned exact and gated for freshness in CI
- **Tailwind CSS v4**, configured by the kit's `tokens.css` (this app adds only `--brand`)
- **React Router**, **react-i18next** (6 locales incl. RTL), **lucide-react** icons

## Design system

**The standard is not here.** This dashboard composes the family kit, so the rules live with the
package: [`docs/ui-standard.md` in qorpe/ui](https://github.com/qorpe/ui/blob/main/docs/ui-standard.md),
with the component inventory generated from its barrel. What this repo decided — which components
came from here, which stayed local, and why — is [ADR 0014](../docs/decisions/0014-adopt-qorpe-ui.md).

`src/index.css` is 21 lines: it imports the kit's `tokens.css` and adds `--brand` (the logo blue,
deliberately outside the kit ramp). Re-skinning stays a one-file change. Dark mode is class-driven
(`.dark`); the semantic status ramp is separate from the accent — both are the kit's rules now.

## Develop

```bash
pnpm install
pnpm dev        # http://localhost:5173, proxies /__admin/* to a Mockifyr host on :8080
pnpm build      # type-check + production build to dist/
```

Run a Mockifyr host alongside (`dotnet run --project src/Mockifyr.Server -- --port 8080`) for live
admin data. In production the built `dist/` is served as static assets by the host.

## Internationalization

Six locales — English, Türkçe, Français, العربية (RTL), 中文, 日本語 — in `src/lib/i18n.ts`,
all fully translated. Switching to Arabic flips the whole layout to RTL via logical CSS properties.
Press **⌘K / Ctrl-K** anywhere for the command palette.

## Deploy

Two options:

- **Embedded in the host** — `pnpm build:embedded` builds the dashboard under the `/__mockifyr/` base;
  run the host with `--dashboard <path-to-dist>` and it is served at `/__mockifyr` (static assets + SPA
  fallback), scoped so the mock-serving surface on every other path is untouched. The repo `Dockerfile`
  does this end-to-end — one image serving the mock engine, admin API, and dashboard.

  ```bash
  docker build -t mockifyr .
  docker run -p 8080:8080 mockifyr           # dashboard at http://localhost:8080/__mockifyr
  ```

- **Standalone** — `pnpm build` (base `/`) and serve `dist/` from any static host / CDN, pointed at a
  Mockifyr host's `/__admin/*` (set up a proxy or CORS as needed).
