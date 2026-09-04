# Quickstart

TerraScale is a cloud database. There is nothing to install locally and no
emulator: `terrascale dev` gives you a personal cloud branch of your project's
database, and dev and prod run the same engine in the same regions.

::: warning Pre-launch
TerraScale is in waitlist beta. Billing is real from day one, with $50 of
credits per beta account. When the docs site is live, every snippet on it runs
against a real sandbox branch pre-filled with your key — that runner is part of
the launch work and not wired yet, so treat snippets here as drafts until then.
:::

## 1. Create an app

```bash
npx create-terrascale-app@latest my-app
cd my-app
```

The starter is a Next.js app wired to `@terrascale/sdk`. Sign in when prompted.
A card is required at signup for fraud screening; you can continue on the free
plan without subscribing, and your first paid month is free if you subscribe
during onboarding.

## 2. Define a schema

Schemas are authored with the typed schema builder in your SDK and applied with
the CLI. `terrascale.schema.json` is the contract: it is what `terrascale push`
applies and what codegen reads.

```ts
import { defineSchema, s } from '@terrascale/sdk'

export const schema = defineSchema({
  collections: {
    posts: {
      fields: {
        title: s.string(),
        authorId: s.string(),
        publishedAt: s.timestamp(),
      },
      indexes: { byAuthor: ['authorId'] },
    },
  },
})
```

```bash
terrascale push        # apply the schema
terrascale codegen     # generate types for your language
```

## 3. Open a cloud dev branch

```bash
terrascale dev --seed ./seed.ts
```

This creates or reuses a personal branch of the project's database in the
nearest region, runs your seed script, tails the log, and watches codegen. Use
`--faults` to test against injected failures. Database and branch creation
takes under 5 seconds.

## 4. Write, then subscribe

Writes are document operations (create/update/patch/delete) — there is no SQL
DML. Queries are SQL, and any query in the live subset can end in
`.subscribe()` for live results delivered over CDN-backed HTTP by default.

```ts
import { TerraScale } from '@terrascale/sdk'

const ts = new TerraScale({ apiKey: process.env.TERRASCALE_API_KEY! })

await ts.from('posts').create({
  title: 'Hello TerraScale',
  authorId: 'mario',
})

// Live query: re-runs never — the server pushes deltas as writes land.
ts
  .from('posts')
  .where(c => c.authorId.eq('mario'))
  .orderBy(c => c.publishedAt.desc())
  .limit(20)
  .subscribe({
    onRows: rows => render(rows),
    onError: err => console.error(err.message, err.fix),
  })
```

Optimistic updates are built into the React hooks (`./react`), and
collection sync feeds the TanStack DB adapter for local-first reads.

## 5. Verify it is live

Open the app in two tabs. Write in one tab; the other tab updates without a
refresh. That round trip — from `npx create-terrascale-app` to a live query —
is the metric we hold ourselves to: under five minutes on a fresh machine.

## Where to go next

- [The two engines](/concepts/engines) — what actually serves your reads and writes
- [Replication model](/concepts/replication) — leaderless active-active, CRDTs, consistency
- [Sync model](/concepts/sync) — shared views, the immutable delta log, the CDN
- [SQL](/concepts/sql) — the live subset, `execute`-only queries, codegen
- [Pricing philosophy](/concepts/pricing-philosophy) — why only writes and storage are billed
