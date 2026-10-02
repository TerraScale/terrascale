---
---
<!-- i18n:fallback:ar -->
> **الترجمة قيد المراجعة.** لا تتوفر بعد مراجعة بشرية لهذه الصفحة بالعربية، لذلك يظهر المحتوى الأصلي بالإنجليزية.

# Quickstart: a live Next.js query

This is the shortest path from a new Next.js app to a cloud-backed live query.
With Node, the TerraScale CLI, and an API key already available, the path is
designed to take less than five minutes. There is no local database process to install:
`terrascale dev` creates or reuses a cloud development branch, and dev and
production use the same engine in the same regions.

::: warning Pre-launch
TerraScale is preparing for public self-service launch. Billing starts when
the service opens; public registration and hosted database access are not
available yet. Runnable snippets require a real cloud sandbox branch
pre-filled with the reader's key; if that branch or its credentials are
unavailable, the runner fails closed rather than substituting a local result.
See [Runnable snippets](/ar/snippets). The remote snippet gate is separate from
this browser quickstart and never substitutes a local result.
:::

## 1. Create an app

```bash
npx create-terrascale-app@latest my-app
cd my-app
npm install
cp .env.example .env.local
```

`terrascale` is a C#/.NET CLI from the separate `ts-cli` repository, not an
npm dependency. No CLI release is published yet; from its checkout, run it with
`dotnet run --project src/TerraScale.Cli -- <command>`, or build it with
`dotnet publish src/TerraScale.Cli/TerraScale.Cli.csproj --configuration Release`
and place the resulting `terrascale` executable on `PATH`. The branch-only
command below uses only the public database-scoped lifecycle:

```bash
# TERRASCALE_API_KEY must be exported for the CLI; .env.local is read by Next.js only.
terrascale dev <database> --watch=false --no-log-tail
```

The command talks to the cloud service. It never starts an engine on your
machine. Data-file seeds (`--seed ./seed.jsonl --seed-collection posts`),
branch diagnostic log tailing, and codegen watch
(`--watch --language typescript --codegen-out <path>`) are optional; the command above disables watch and log tailing. The CLI does
not execute TypeScript seed functions.

The starter is a Next.js app wired to `@terrascale/sdk`. Click **Sign in with
TerraScale** in each browser tab. It uses OAuth authorization-code PKCE and
exchanges the signed-in credential for short-lived access scoped to the
selected project/database and the `posts` collection’s read/write operations.
It renews that access while the sign-in token remains valid. Both credentials
stay in tab memory until disconnect or reload. Never embed a privileged key
in `NEXT_PUBLIC` settings. The deployed provider must accept the exact
registered callback and authorize the selected branch. Application sign-in
integration and the real five-minute journey remain unqualified.
A card is required at signup for fraud screening; you can continue on the Hobby
plan without subscribing, and the onboarding promotion waives only the first paid month’s
subscription fee. Public Alpha plans use hard allowances with no paid overages.
Hobby and promotional eligibility are limited per verified billing customer
across organizations; review [billing terms](/ar/pricing/) before enrollment.

## 2. Define a schema

Schemas are authored with the typed schema builder in your SDK and applied with
the CLI. `terrascale.schema.json` is the contract: it is what `terrascale push`
applies and what codegen reads.

```ts
import { defineSchema, string, timestamp } from '@terrascale/sdk'

export const schema = defineSchema({
    posts: {
      fields: {
        title: string().required(),
        authorId: string().required().indexed(),
        publishedAt: timestamp().required(),
      },
      indexes: { byAuthor: ['authorId'] },
    },
})

// The SQL builder takes schema field objects, not column-name strings.
export const posts = schema.toObject().collections.find((c) => c.name === 'posts')!
export const title = posts.fields.find((f) => f.name === 'title')!
export const publishedAt = posts.fields.find((f) => f.name === 'publishedAt')!
```

```bash
terrascale push --database <branch-database> # applies schema to that branch
terrascale codegen --language typescript --out schema.gen.ts # types from the schema file
```

`codegen` defaults to C# on standard output, so pass `--language typescript`
and an `--out` path for a TypeScript app.

## 3. Open a cloud dev branch

This creates or reuses a cloud branch of the named database. It prints the
branch name, source, and selected region; it does not print credentials or a
database id. Use the dashboard/API response to obtain the branch database id,
then set the public project ID, branch database ID, API URL, auth URL, and
exact registered callback URL in `.env.local` (`.env.example` lists the names).
The API URL stays the global origin; the SDK consults discovery at
atlas.terrascale.tech automatically, once per database, and then calls
regional endpoints directly — no proxy fallback exists, and a failed
discovery falls back to the region catalog (first available regional
endpoint).
Export `TERRASCALE_API_KEY` separately for CLI commands; no API key belongs
in the browser’s public settings. The branch runs in the cloud
service, not in a local database process or customer-run emulator.

## 4. Attach a live query

Queries use the SDK's schema-bound, typed SQL builder. It emits parameterized,
read-only `SELECT` SQL; there is no TQL or collection-query facade. The
reactive client attaches the builder's descriptor and requires `databaseId`.
The example below assumes sign-in has supplied `branchAccessToken`; the
generated app implements the exchange and renewal in `app/page.tsx`:

```ts
import { Direction, select, TerraScaleSyncV1Client } from '@terrascale/sdk'
import { posts, title, publishedAt } from './schema'

const databaseId = process.env.NEXT_PUBLIC_TERRASCALE_DATABASE_ID!
const sync = new TerraScaleSyncV1Client({
  apiBaseUrl: process.env.NEXT_PUBLIC_TERRASCALE_API_URL,
  accessToken: branchAccessToken, // short-lived delegated access from sign-in
})

const postsQuery = select(posts)
  .columns([title, publishedAt])
  .orderByField(publishedAt, Direction.Desc)
  .limit(20)
  .subscribe()
const parameters = new TextEncoder().encode(JSON.stringify(
  postsQuery.parameters,
))

const live = await sync.subscribe({
  databaseId,
  subscription: 'posts',
  query: postsQuery.sql,
  parameters,
})
const stop = live.view.subscribe(() => {
  // Projected fields are canonical JSON bytes; decode at the browser boundary.
  render(live.view.getVisibleItems())
})

// Checkpoints are opaque. Persist this value if you want to resume later;
// never inspect or construct its bytes.
const checkpoint = live.checkpoint()
if (live.reset) {
  console.info(`live view reset: ${live.reset.reason}`)
}

// On a later attach, a stale checkpoint (retention, schema, rules, or branch
// rebase) is reported as a typed reset. The SDK installs a fresh authorized
// snapshot and exposes the reason; reconnect is only needed for a manually
// controlled transport.
const resumed = await sync.subscribe({
  databaseId, subscription: 'posts-resumed', query: postsQuery.sql, parameters,
  ...(checkpoint === undefined ? {} : { checkpoint }),
})
if (resumed.reset) console.info(`resumed with fresh snapshot: ${resumed.reset.reason}`)
resumed.close()

// On teardown: stop(); live.close()
```

`Query.raw(sql, parameters)` remains available for a validated, parameterized
read-only statement when a schema-bound builder is not practical. It cannot
perform DDL or DML. The starter uses the typed builder and the public `client.documents.put`
operation for document writes.

## 5. Verify it is live

Open the app in two tabs and sign in separately to connect each to the same
cloud branch. Add a post in either tab; both live views
should display it. This journey still requires real cloud qualification.
The CRUD snippet exercises the public write API when its real sandbox is available;
see [pricing](/ar/pricing/) for billing (reads and writes consume operation
units, and logical document/history storage counts in every selected Region).

## Where to go next

- [Replication and durability](/ar/concepts/replication) — sessions, acknowledgements, and conflicts
- [Sync model](/ar/concepts/sync) — shared views, the immutable delta log, the CDN
- [SQL](/ar/concepts/sql) — the live subset, `execute`-only queries, codegen
- [Pricing philosophy](/ar/concepts/pricing-philosophy) — why only writes and storage are billed
- [Runnable snippets](/ar/snippets) — CRUD, SQL, and live subscriptions against a
  real sandbox; unavailable credentials fail the remote gate
