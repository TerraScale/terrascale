# `create-terrascale-app`

Scaffold a Next.js + TerraScale development app with document writes and a
live query. The five-minute sign-in journey still requires qualification.

## Use it

```bash
npx create-terrascale-app@latest my-app
cd my-app
npm install
cp .env.example .env.local
terrascale dev <database> --watch=false --no-log-tail
# Set the branch database and project settings in .env.local.
terrascale push --database <branch-database>
npm run dev
```

The CLI is a C#/.NET tool from the separate `ts-cli` repository; it is not an
npm dependency. Until a CLI release is published, build it from a checkout and
place the resulting `terrascale` executable on `PATH`:

```bash
cd /path/to/ts-cli
dotnet publish src/TerraScale.Cli/TerraScale.Cli.csproj --configuration Release
```

Alternatively, run it in place with
`dotnet run --project src/TerraScale.Cli -- <command>`.

`terrascale dev <database> --watch=false --no-log-tail` creates or reuses a
cloud branch. It prints the branch name, source, and selected region, but not
credentials or a database id. Use the dashboard/API response to obtain the
branch database id, then put that id and the API URL in `.env.local`.
Click **Sign in with TerraScale** in each browser tab. The starter uses
authorization-code PKCE (S256); the resulting credential stays in tab memory
and is cleared on disconnect/reload. Set the exact registered callback URL
in `.env.local`. Export a scoped key as
`TERRASCALE_API_KEY` (or pass
`--api-key`) for the CLI; `.env.local` is read by Next.js, not by the shell.
The CLI supports data-file seeds (`--seed ./seed.jsonl --seed-collection posts`),
bounded branch diagnostics, and codegen watch. It does not run TypeScript seed
functions. Apply `terrascale.schema.json` to the branch with
`terrascale push --database <branch-database>` before opening the app.
The starter attaches a typed SQL live query through the current reactive SDK;
it writes posts through the published typed `client.documents.put` API. There is no local engine or fake SDK involved.

- Sign in when prompted. A card is required at signup for fraud screening;
  you can continue on Free while eligible. The eligible onboarding promotion
  waives only the subscription fee; explicitly authorized excess is billed.
- There is nothing to install locally: TerraScale has no customer-run emulator
  or local database process. `terrascale dev` gives you a personal cloud branch
  of your project's database, and dev and prod run the same engine in the same
  regions (D-18).
- Open the app in two tabs to attach the same cloud-backed live result view.
  Add a post in either tab to exercise a real write and live delivery.
  Sign in separately in each tab.

## What it scaffolds (`template/`)

- Next.js app with `app/page.tsx`: a schema-backed SQL builder and live
  `TerraScaleSyncV1Client.subscribe` query over CDN-backed HTTP by default
  (WebSocket opt-in), scoped by `databaseId`.
- `auth.ts` — in-memory OAuth refresh rotation and scoped database delegation.
- `schema.ts`: the typed `defineSchema` source used by the SQL builder.
- `terrascale.schema.json`: the contract `terrascale push` applies and
  codegen reads (typed `defineSchema` builder in your code emits it).
- `seed.ts`: optional cloud seed function for an application script runner;
  the CLI accepts data files, not this function.
- `AGENTS.md`: starter-owned assistant brief (SDK, schema/SQL builders,
  `terrascale dev`, MCP setup) so AI-assisted builders work on minute one.
- `.env.example`: public project ID, database ID, and API URL settings; no key.

## Status

The template pins the current SDK surface (`@terrascale/sdk` 0.3.0: schema/SQL
builders, typed document writes, and `TerraScaleSyncV1Client`). Tenant
provisioning and application sign-in remain unqualified. The OAuth sign-in
flow requires the deployed authorization provider to accept the registered
callback and authorize the resulting bearer for the selected cloud branch.
Full docs:
https://docs.terrascale.tech/getting-started/

Application sign-in to live updates remains an unmet launch journey: the
production auth/delegation binding and real cloud credentials are still
required. Never embed privileged API keys in NEXT_PUBLIC settings. Browser
credentials must be limited to this development branch and its required
posts read/write/reactive permissions. Local checks do not prove real cloud
behavior or the five-minute setup target.

After OAuth sign-in, the starter exchanges its bearer for short-lived access
limited to the selected project/database and the `posts` collection, with read
and write actions. It renews OAuth access with a rotating refresh token and renews the scoped
delegation as needed. If authorization expires, is revoked, or a refresh
response is lost, disconnect and sign in again. All credentials remain in tab
memory; disconnect cancels exchanges and clears them.
