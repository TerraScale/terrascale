---
---
<!-- i18n:fallback:fr -->
> **Traduction en attente.** Cette page n’a pas encore été relue en français et affiche le contenu original en anglais.

# Quickstart: a live Next.js query

This is the shortest path from a new Next.js app to a cloud-backed live query.
With Node, the TerraScale CLI, and an API key already available, the path is
designed to take less than five minutes. There is nothing to install locally:
`terrascale dev` creates or reuses a cloud development branch, and dev and
production use the same engine in the same regions.

::: warning Pre-launch
TerraScale is preparing for public self-service launch. Billing starts when
the service opens. Runnable snippets require a real cloud sandbox branch
pre-filled with the reader's key; if that branch or its credentials are
unavailable, the runner fails closed rather than substituting a local result.
See [Runnable snippets](/fr/snippets). The remote snippet gate is separate from
this browser quickstart and never substitutes a local result.
:::

## 1. Create an app

```bash
npx create-terrascale-app@latest my-app
cd my-app
npm install
cp .env.example .env.local
```

`terrascale` is a Go binary from the separate `ts-cli` repository, not an npm
dependency. Install it from a CLI release (or, while developing the CLI, from
its checkout with `go install ./cmd/terrascale`). The branch-only command below
uses only the public database-scoped lifecycle:

```bash
# TERRASCALE_API_KEY must be exported for the CLI; .env.local is read by Next.js only.
terrascale dev <database> --watch=false --no-log-tail
```

The command talks to the cloud service. It never starts an engine on your
machine. Seed execution, log tailing, and codegen watch are separate CLI
capabilities; do not add those flags unless the CLI release you installed
advertises their contracts.

The starter is a Next.js app wired to `@terrascale/sdk`. Sign in when prompted.
A card is required at signup for fraud screening; you can continue on the free
plan without subscribing, and the onboarding promotion waives only the first paid month’s
subscription fee. Explicitly authorized excess usage remains chargeable.
Free and promotional eligibility are limited per verified billing customer
across organizations; review [billing terms](/fr/pricing/) before enrollment.

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
```

```bash
terrascale push        # applies the schema when the CLI contract is published
terrascale codegen     # generates types when the CLI contract is published
```

## 3. Open a cloud dev branch

This creates or reuses a cloud branch of the named database. It prints the
branch name, source, and selected region; it does not print credentials or a
database id. Use the dashboard/API response to obtain the branch database id,
then put that id, the API URL, and a scoped API key in the generated
`.env.local` (the `.env.example` lists the names). The branch runs in the cloud
service, not in a local database process or customer-run emulator.

## 4. Attach a live query

Queries use the SDK's schema-bound, typed SQL builder. It emits parameterized,
read-only `SELECT` SQL; there is no TQL or collection-query facade. The
reactive client attaches the builder's descriptor and requires `databaseId`:

```ts
import { Direction, select, TerraScaleSyncV1Client } from '@terrascale/sdk'
import { posts, title, publishedAt } from './schema'

const databaseId = process.env.NEXT_PUBLIC_TERRASCALE_DATABASE_ID!
const sync = new TerraScaleSyncV1Client({
  apiBaseUrl: process.env.NEXT_PUBLIC_TERRASCALE_API_URL,
  accessToken: process.env.NEXT_PUBLIC_TERRASCALE_API_KEY,
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
perform DDL or DML. The starter uses the typed builder; document writes stay
blocked until the
published Database API contract is available.

## 5. Verify it is live

Open the app in two tabs to attach the same CDN-backed live result view. The
starter does not perform writes while the typed Database API contract is
pending. The CRUD snippet demonstrates writes against the qualified sandbox;
see [pricing](/fr/pricing/) for billing (writes and logical document/history
storage are billed; reads, queries, egress, delivery, fan-out, and subscribers
are free).

## Where to go next

- [The two engines](/fr/concepts/engines) — what actually serves your reads and writes
- [Replication model](/fr/concepts/replication) — leaderless active-active, CRDTs, consistency
- [Sync model](/fr/concepts/sync) — shared views, the immutable delta log, the CDN
- [SQL](/fr/concepts/sql) — the live subset, `execute`-only queries, codegen
- [Pricing philosophy](/fr/concepts/pricing-philosophy) — why only writes and storage are billed
- [Runnable snippets](/fr/snippets) — CRUD, SQL, and live subscriptions against a
  real sandbox; unavailable credentials fail the remote gate
