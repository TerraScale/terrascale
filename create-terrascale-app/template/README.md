# Starter app (from `create-terrascale-app`)

Next.js + `@terrascale/sdk` with a first live query. There is nothing to
install locally: TerraScale has no customer-run emulator or local database
process. `terrascale dev` gives you a personal cloud branch of this project's
database.

## Run it (under 5 minutes, D-74)

```bash
npm install
npm run dev
```

`terrascale` is the Go CLI from the separate `ts-cli` repository, not an npm
package. Install it from a checkout with `go install ./cmd/terrascale`, then
run `terrascale dev <database> --watch=false --no-log-tail` to create or reuse
a cloud branch. Seed execution is pending the published CLI seed contract.

Open the app in two tabs. The starter writes through the typed document API in
one tab and both tabs update from the same cloud-backed live result view.

## Files

- `app/page.tsx` — schema-backed SQL builder, document write, and first live
  query via `TerraScaleSyncV2Client.subscribe({ databaseId, ... })` over
  CDN-backed HTTP.
- `schema.ts` — the typed `defineSchema` source for the JSON contract.
- `terrascale.schema.json` — the contract `terrascale push` applies.
- `seed.ts` — optional cloud seed entry point for the CLI seed contract when
  seed execution is enabled.
- `AGENTS.md` — SDK, schema/SQL builders, `terrascale dev`, MCP setup.

Full quickstart: https://docs.terrascale.tech/getting-started/
