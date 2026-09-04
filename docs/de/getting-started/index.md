---
---
<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

# Quickstart

TerraScale is a cloud database. There is nothing to install locally: no
customer-run emulator or local database process. `terrascale dev` gives you a
personal cloud branch of your project's database, and dev and prod run the same
engine in the same regions.

::: warning Pre-launch
TerraScale is in waitlist beta. Billing is real from day one, with $50 of
credits per beta account. Every snippet runs against a real cloud sandbox branch
pre-filled with the reader's key; if that branch or its credentials are
unavailable, the runner fails closed rather than substituting a local result.
See [Runnable snippets](/de/snippets).
:::

## 1. Create an app

```bash
npx create-terrascale-app@latest my-app
cd my-app
```

`terrascale` is a Go binary from the separate `ts-cli` repository, not an npm
dependency. Install it from a checkout with `go install ./cmd/terrascale`.
The currently published CLI branch lifecycle is
`terrascale dev <database> --watch=false --no-log-tail`; seed execution,
log tailing, and codegen watch are pending their public CLI contracts.

The starter is a Next.js app wired to `@terrascale/sdk`. Sign in when prompted.
A card is required at signup for fraud screening; you can continue on the free
plan without subscribing, and your first paid month is free if you subscribe
during onboarding.

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

```bash
terrascale dev <database> --watch=false --no-log-tail
```

This creates or reuses a cloud branch of the named database. The current CLI
does not claim seed execution, log tailing, codegen watch, or fault injection;
those flags fail closed until their public routes exist. The branch runs in the
cloud service, not in a local database process or customer-run emulator.

## 4. Attach a live query

The current SDK exposes typed document operations, schema/SQL builders, named
management clients, and a reactive `TerraScaleSyncV2Client`. A live query uses
the exact current SDK call below and must include `databaseId`:

```ts
import { TerraScaleSyncV2Client } from '@terrascale/sdk'

const databaseId = process.env.TERRASCALE_DATABASE_ID!
const sync = new TerraScaleSyncV2Client({
  apiBaseUrl: process.env.TERRASCALE_API_URL,
  accessToken: process.env.TERRASCALE_API_KEY,
})

const live = await sync.subscribe({
  databaseId,
  subscription: 'posts',
  query: 'SELECT * FROM posts ORDER BY publishedAt DESC LIMIT 20',
  parameters: new Uint8Array(),
})
const stop = live.view.subscribe(() => {
  // Projected fields are canonical JSON bytes; parse at the browser boundary.
  render(live.view.getVisibleItems())
})

// On teardown: stop(); live.close()
```

`Query.raw(sql, parameters).execute()` and `.subscribe()` create validated
descriptors for the same read-only SQL contract. The starter's schema-bound
builder follows the same pattern and its document write uses
`client.documents.put`.

## 5. Verify it is live

Open the app in two tabs. Add a post in one tab; the document write is served by
the regional API and both tabs update from the same CDN-backed live result view.

## Where to go next

- [The two engines](/de/concepts/engines) — what actually serves your reads and writes
- [Replication model](/de/concepts/replication) — leaderless active-active, CRDTs, consistency
- [Sync model](/de/concepts/sync) — shared views, the immutable delta log, the CDN
- [SQL](/de/concepts/sql) — the live subset, `execute`-only queries, codegen
- [Pricing philosophy](/de/concepts/pricing-philosophy) — why only writes and storage are billed
- [Runnable snippets](/de/snippets) — CRUD, SQL, and live subscriptions; pending
  tenant, branch, backup, and export fixtures are marked as non-runnable
