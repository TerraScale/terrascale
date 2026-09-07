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
the dashboard/API response to obtain the branch database id, then put that id
and the API settings in `.env.local`. Set the redirect URI to the exact URL
registered for `terrascale-starter-web`; sign-in uses authorization-code PKCE
(S256) and requests only `openid database:read database:write`. The resulting
credential stays in tab memory and is passed directly to `@terrascale/sdk`; it
is never rendered or persisted. Export a key as `TERRASCALE_API_KEY` (or pass
`--api-key`) for the CLI; `.env.local` is read by Next.js, not by the shell.
Seed execution, log tailing, and codegen watch require their published CLI
capabilities; this starter never starts a local engine.

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

The Dashboard's first-party client must be configured with the exact callback
URI and a live session/delegation binding before production use. Never embed
privileged API keys in NEXT_PUBLIC settings. Browser credentials must be
limited to this development branch and its required posts read/write/reactive
permissions. Local checks do not prove real cloud behavior or the five-minute
setup target.
