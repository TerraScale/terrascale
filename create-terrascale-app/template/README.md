# Starter app (from `create-terrascale-app`)

Next.js + `@terrascale/sdk` with a first live query. There is nothing to
install locally: TerraScale has no customer-run emulator or local database
process. `terrascale dev` gives you a personal cloud branch of this project's
database.

## Run the development connection

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
the API URL in `.env.local`. Enter a scoped development credential in each
browser tab; it stays only in memory until disconnect/reload. Export a key as
`TERRASCALE_API_KEY` (or pass `--api-key`) for the CLI; `.env.local` is read by
Next.js, not by the shell. Seed execution, log tailing, and codegen watch
require their published CLI capabilities; this starter never starts a local
engine.

Open the app in two tabs to attach the same cloud-backed live result view. The
starter can add posts through the typed Database API contract. The cloud
seed CLI contract remains pending; the Add post form needs no seed runner.

## Files

- `app/page.tsx` — schema-backed SQL builder and first live query via
  `TerraScaleSyncV1Client.subscribe({ databaseId, ... })` over CDN-backed HTTP.
- `schema.ts` — the typed `defineSchema` source for the JSON contract.
- `terrascale.schema.json` — the contract `terrascale push` applies.
- `seed.ts` — optional cloud seed entry point for the CLI seed contract when
  seed execution is enabled.
- `AGENTS.md` — SDK, schema/SQL builders, `terrascale dev`, MCP setup.

Full quickstart: https://docs.terrascale.tech/getting-started/

Application sign-in to live updates remains an unmet launch journey: the
production auth/delegation binding and real cloud credentials are still
required. Never embed privileged API keys in NEXT_PUBLIC settings. Browser
credentials must be limited to this development branch and its required
posts read/write/reactive permissions. Local checks do not prove real cloud
behavior or the five-minute setup target.
