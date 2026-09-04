# `create-terrascale-app` (P-8/D-56/D-74)

Scaffold a Next.js + TerraScale app with a first live query in under five
minutes once Node, the Go CLI, and a TerraScale API key are available.

## Use it

```bash
npx create-terrascale-app@latest my-app
cd my-app
npm install
cp .env.example .env.local
terrascale dev <database> --watch=false --no-log-tail
npm run dev
```

The CLI is a Go binary from the separate `ts-cli` repository; it is not an
npm dependency. Until a CLI release is published, install it from a checkout:

```bash
cd /path/to/ts-cli
go install ./cmd/terrascale
```

`terrascale dev <database> --watch=false --no-log-tail` creates or reuses a
cloud branch. It prints the branch name, source, and selected region, but not
credentials or a database id. Use the dashboard/API response to obtain the
branch database id, then put that id, the API URL, and a scoped API key in
`.env.local`. Export the same key as `TERRASCALE_API_KEY` (or pass
`--api-key`) for the CLI; `.env.local` is read by Next.js, not by the shell.
Seed execution, log tailing, and codegen watch are separate CLI capabilities;
use those flags only when the installed CLI release advertises their contracts.
The starter attaches a typed SQL live query through the current reactive SDK;
it does not invent a document-write helper while that public contract is
pending. There is no local engine or fake SDK involved.

- Sign in when prompted. A card is required at signup for fraud screening;
  you can continue on the free plan without subscribing, and your first paid
  month is free if you subscribe during onboarding.
- There is nothing to install locally: TerraScale has no customer-run emulator
  or local database process. `terrascale dev` gives you a personal cloud branch
  of your project's database, and dev and prod run the same engine in the same
  regions (D-18).
- Open the app in two tabs to attach the same cloud-backed live result view.
  Seed execution remains blocked until its CLI contract is published.

## What it scaffolds (`template/`)

- Next.js app with `app/page.tsx`: a schema-backed SQL builder and live
  `TerraScaleSyncV2Client.subscribe` query over CDN-backed HTTP by default
  (WebSocket opt-in), scoped by `databaseId`.
- `schema.ts`: the typed `defineSchema` source used by the SQL builder.
- `terrascale.schema.json`: the contract `terrascale push` applies and
  codegen reads (typed `defineSchema` builder in your code emits it).
- `seed.ts`: retained as the cloud-only seed entry point for the CLI contract
  when seed execution is enabled; it never starts a local engine.
- `AGENTS.md`: starter-owned assistant brief (SDK, schema/SQL builders,
  `terrascale dev`, MCP setup) so AI-assisted builders work on minute one.
- `.env.example`: API key, project ID, database ID, and API URL settings.

## Status

The template pins the current SDK surface (`@terrascale/sdk` 0.3.0: schema/SQL
builders and `TerraScaleSyncV2Client`). Typed document writes and tenant
provisioning remain outside the starter until their owning contracts are
published.
Full docs:
https://docs.terrascale.tech/getting-started/
