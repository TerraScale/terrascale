# `create-terrascale-app` (P-8/D-56/D-74)

Scaffold a Next.js + TerraScale app with a first live query in under
5 minutes on a fresh machine.

## Use it

```bash
npx create-terrascale-app@latest my-app
cd my-app
npm install
npm run dev
```

The CLI is a Go binary from the separate `ts-cli` repository; it is not an
npm dependency. Until a CLI release is published, install it from a checkout:

```bash
cd /path/to/ts-cli
go install ./cmd/terrascale
```

The current public CLI can create or reuse a cloud branch with
`terrascale dev <database> --watch=false --no-log-tail`. Its `--seed`, log-tail,
and codegen-watch flags are intentionally rejected until those public routes
are published. The starter writes through `client.documents.put`, so no local
engine or fake SDK is involved.

- Sign in when prompted. A card is required at signup for fraud screening;
  you can continue on the free plan without subscribing, and your first paid
  month is free if you subscribe during onboarding.
- There is nothing to install locally: TerraScale has no customer-run emulator
  or local database process. `terrascale dev` gives you a personal cloud branch
  of your project's database, and dev and prod run the same engine in the same
  regions (D-18).
- Open the app in two tabs. Add a post in one tab and both tabs update from the
  same cloud-backed live result view.

## What it scaffolds (`template/`)

- Next.js app with `app/page.tsx`: a schema-backed SQL builder, cloud document
  write, and live `TerraScaleSyncV2Client.subscribe` query over CDN-backed HTTP
  by default (WebSocket opt-in), scoped by `databaseId`.
- `schema.ts`: the typed `defineSchema` source used by the SQL builder.
- `terrascale.schema.json`: the contract `terrascale push` applies and
  codegen reads (typed `defineSchema` builder in your code emits it).
- `seed.ts`: retained as the cloud-only seed entry point for the CLI contract
  when seed execution is enabled; it never starts a local engine.
- `AGENTS.md`: starter-owned assistant brief (SDK, schema/SQL builders,
  `terrascale dev`, MCP setup) so AI-assisted builders work on minute one.
- `.env.example`: API key, project ID, database ID, and API URL settings.

## Status

The template pins the current SDK surface (`@terrascale/sdk` 0.3.0: typed
document operations, schema/SQL builders, and `TerraScaleSyncV2Client`). Tenant
provisioning remains outside the starter until its owning contract is published.
Full docs:
https://docs.terrascale.tech/getting-started/
