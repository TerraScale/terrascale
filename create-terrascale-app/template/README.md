# Starter app (from `create-terrascale-app`)

Next.js + `@terrascale/sdk` with a first live query. There is nothing to
install locally and no emulator: `terrascale dev` gives you a personal cloud
branch of this project's database.

## Run it (under 5 minutes, D-74)

```bash
npm install
npm run dev
```

`terrascale` is the Go CLI from the separate `ts-cli` repository, not an npm
package. Install it from a checkout with `go install ./cmd/terrascale`, then
run `terrascale dev <database> --watch=false --no-log-tail` to create or reuse
a cloud branch. Seed execution is pending the published CLI seed contract.

Open the app to attach the live result view. Document writes are not shown
until the typed Database API contract is published.

## Files

- `app/page.tsx` — first live query via
  `TerraScaleSyncV2Client.subscribe({ databaseId, ... })` over CDN-backed HTTP.
- `terrascale.schema.json` — the contract `terrascale push` applies.
- `seed.ts` — placeholder for the not-yet-published CLI seed contract.
- `AGENTS.md` — SDK, schema/SQL builders, `terrascale dev`, MCP setup.

Full quickstart: https://docs.terrascale.tech/getting-started/
