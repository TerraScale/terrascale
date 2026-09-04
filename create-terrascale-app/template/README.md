# Starter app (from `create-terrascale-app`)

Next.js + `@terrascale/sdk` with a first live query. There is nothing to
install locally: TerraScale has no customer-run emulator or local database
process. `terrascale dev` gives you a personal cloud branch of this project's
database.

## Run it (under 5 minutes, D-74)

```bash
npm install
cp .env.example .env.local
terrascale dev <database> --watch=false --no-log-tail
npm run dev
```

`terrascale` is the Go CLI from the separate `ts-cli` repository, not an npm
package. Install it from a checkout with `go install ./cmd/terrascale`, then
run the command above to create or reuse a cloud branch. It prints the branch
name, source, and selected region, but not credentials or a database id. Use
the dashboard/API response to obtain the branch database id, then put that id,
the API URL, and a scoped API key in `.env.local`. Export the same key as
`TERRASCALE_API_KEY` (or pass `--api-key`) for the CLI; `.env.local` is read by
Next.js, not by the shell. Seed execution, log tailing, and codegen watch
require their published CLI capabilities; this starter never starts a local
engine.

Open the app in two tabs to attach the same cloud-backed live result view. The
starter does not add document writes while the typed Database API contract is
pending; seed execution is also pending its CLI contract.

## Files

- `app/page.tsx` — schema-backed SQL builder and first live query via
  `TerraScaleSyncV2Client.subscribe({ databaseId, ... })` over CDN-backed HTTP.
- `schema.ts` — the typed `defineSchema` source for the JSON contract.
- `terrascale.schema.json` — the contract `terrascale push` applies.
- `seed.ts` — optional cloud seed entry point for the CLI seed contract when
  seed execution is enabled.
- `AGENTS.md` — SDK, schema/SQL builders, `terrascale dev`, MCP setup.

Full quickstart: https://docs.terrascale.tech/getting-started/
