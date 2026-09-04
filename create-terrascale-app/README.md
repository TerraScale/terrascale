# `create-terrascale-app` (P-8/D-56/D-74)

Scaffold a Next.js + TerraScale app with a first live query in under
5 minutes on a fresh machine.

## Use it

```bash
npx create-terrascale-app@latest my-app
cd my-app
npm install
terrascale dev --seed ./seed.ts
npm run dev
```

- Sign in when prompted. A card is required at signup for fraud screening;
  you can continue on the free plan without subscribing, and your first paid
  month is free if you subscribe during onboarding.
- There is nothing to install locally and no emulator: `terrascale dev`
  gives you a personal cloud branch of your project's database, and dev and
  prod run the same engine in the same regions (D-18).
- Open the app in two tabs. Write in one tab; the other tab updates without
  a refresh. That round trip is the metric we hold ourselves to (D-74).

## What it scaffolds (`template/`)

- Next.js app with `app/page.tsx`: create + live `.subscribe()` query over
  CDN-backed HTTP by default (WebSocket opt-in).
- `terrascale.schema.json`: the contract `terrascale push` applies and
  codegen reads (typed `defineSchema` builder in your code emits it).
- `seed.ts`: seed for `terrascale dev --seed ./seed.ts`.
- `AGENTS.md`: starter-owned assistant brief (SDK, schema/SQL builders,
  `terrascale dev`, MCP setup) so AI-assisted builders work on minute one.
- `.env.example`: `TERRASCALE_API_KEY` / `NEXT_PUBLIC_TERRASCALE_API_KEY`.

## Status

Stub for day one (P-8). The template tracks the pinned SDK surface
(`@terrascale/sdk`: CRUD, typed SQL builder, `subscribe`/`execute`,
tenants via `ts.tenants.create(key)` → `POST /api/v1/tenants`). Full docs:
https://docs.terrascale.tech/getting-started/
