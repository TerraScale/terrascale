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
# Set the branch database and project settings in .env.local.
terrascale push --database <branch-database>
npm run dev
```

`terrascale` is the C#/.NET CLI from the separate `ts-cli` repository, not an
npm package. From that checkout, run the pinned project directly:

```bash
dotnet run --project src/TerraScale.Cli -- dev <database> --watch=false --no-log-tail
```

For a globally available command, publish the same project and place the
resulting executable on `PATH`:

```bash
dotnet publish src/TerraScale.Cli/TerraScale.Cli.csproj --configuration Release
```

The CLI creates or reuses a cloud branch. It prints the branch
name, source, and selected region, but not credentials or a database id. Use
the dashboard/API response to obtain the branch database id, then put that id
and the API settings in `.env.local`. Set the redirect URI to the exact URL
registered for `terrascale-starter-web`; sign-in uses authorization-code PKCE
(S256) and requests only `openid offline_access database:read database:write`. The resulting
credential stays in tab memory and is passed directly to `@terrascale/sdk`; it
is never rendered or persisted. Export a key as `TERRASCALE_API_KEY` (or pass
`--api-key`) for the CLI; `.env.local` is read by Next.js, not by the shell.
Apply the collection schema to that branch before attaching the live query:
`terrascale push --database <branch-database>`. The CLI supports data-file
seeds, bounded branch diagnostics, and codegen watch; it does not execute
TypeScript seed functions.

Open the app in two tabs to attach the same cloud-backed live result view. The
starter can add posts through the typed Database API contract. The Add post form needs no seed runner. Sign in separately in each tab.

## Files

- `app/page.tsx` — schema-backed SQL builder and first live query via
  `TerraScaleSyncV1Client.subscribe({ databaseId, ... })` over CDN-backed HTTP.
- `auth.ts` — in-memory OAuth refresh rotation and scoped database delegation.
- `schema.ts` — the typed `defineSchema` source for the JSON contract.
- `terrascale.schema.json` — the contract `terrascale push` applies.
- `seed.ts` — optional cloud seed function for an application script runner.
  `terrascale dev --seed` accepts data files, not TypeScript functions.
- `AGENTS.md` — SDK, schema/SQL builders, `terrascale dev`, MCP setup.

Full quickstart: https://docs.terrascale.tech/getting-started/

The Dashboard's first-party client must be configured with the exact callback
URI and a live session/delegation binding before production use. Never embed
privileged API keys in NEXT_PUBLIC settings. Browser credentials must be
limited to this development branch and its required posts read/write/reactive
permissions. Local checks do not prove real cloud behavior or the five-minute
setup target.

After OAuth sign-in, the starter exchanges its bearer for short-lived access
limited to the selected project/database and the `posts` collection, with read
and write actions. It renews OAuth access with a rotating refresh token and renews the scoped
delegation as needed. If authorization expires, is revoked, or a refresh
response is lost, disconnect and sign in again. All credentials remain in tab
memory; disconnect cancels exchanges and clears them.
