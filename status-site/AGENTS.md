# ts-status

Read the root [AGENTS.md](../AGENTS.md).

TypeScript Cloudflare Worker with server-rendered HTML, Vite Plus and the Cloudflare Vite plugin; no frontend framework. Plain CSS uses dashboard design tokens and Lightning CSS.

## Layout and checks

- `src/`: application, probes, API, admin and database code; `schema.sql`: D1 storage; `public/`: static assets; `scripts/`: canary recovery tools.
- Install: `npm ci`. Build: `npm run build`. Type check: `npm run check`. Lint: `npm run lint`. From the monorepo root: `make status-build`.

## Rules

- Status history, incidents, subscriptions and probe coordination stay on D1 (`STATUS_DB`) and Durable Objects. This deliberate Ouroboros exception must keep working during a TerraBase outage; never move its data path to TerraBase.
- `ts-orch` is authoritative for regional origins. Discover all public regions and placement changes automatically; never keep a static list, copy probe credentials or infer DNS names from region identities. The public `api.terrascale.tech` host supports regional readiness assertions.
- Every region serves automatically when `ts-orch` reports it ready, with no enable step.
