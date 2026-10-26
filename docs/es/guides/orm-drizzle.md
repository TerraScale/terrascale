---
title: Use Drizzle ORM
description: Connect Drizzle ORM to a TerraBase database with @libsql/client, generate and apply migrations with drizzle-kit, and run CRUD, relational queries and transactions.
---

<!-- i18n:fallback:es -->
> **Traducción pendiente.** Esta página aún no tiene revisión humana en español y muestra el contenido original en inglés.

<div lang="en" dir="ltr">

# Use Drizzle ORM

Drizzle talks to TerraBase through `@libsql/client` over the libSQL remote protocol. You keep Drizzle's SQLite schema, its migrations and its query API; you change the URL and the token. Read [Connect with libSQL clients](/es/guides/libsql-clients) for the URL, tokens and supported SQL.

A complete app is in [`examples/orm-drizzle`](https://github.com/TerraScale/ts-docs/tree/main/examples/orm-drizzle): schema, migration, CRUD, a relation and transactions.

## Install

```sh
npm install drizzle-orm @libsql/client
npm install -D drizzle-kit
```

## Connect

Create an API key with **Read and write** access to the database in the dashboard, and exchange it for an access token ([Authentication](/es/guides/authentication)). Keep the key on your server.

```js
// src/db.js
import { createClient } from '@libsql/client'
import { drizzle } from 'drizzle-orm/libsql'
import * as schema from './schema.js'

export async function connect() {
  const client = createClient({
    // Keep the trailing slash.
    url: `https://api.terrascale.tech/v1/projects/${process.env.TERRASCALE_PROJECT_ID}/databases/${process.env.TERRASCALE_DATABASE}/libsql/`,
    authToken: await accessToken(),
  })
  return drizzle(client, { schema })
}

// Client credentials exchange: the key ID is the part before "_".
async function accessToken(apiKey = process.env.TERRASCALE_API_KEY) {
  const keyId = apiKey.slice(0, apiKey.indexOf('_'))
  const response = await fetch('https://auth.terrascale.tech/terrascale/oauth/token', {
    method: 'POST',
    headers: { authorization: 'Basic ' + Buffer.from(`${keyId}:${apiKey}`).toString('base64') },
    body: new URLSearchParams({ grant_type: 'client_credentials' }),
  })
  if (!response.ok) throw new Error(`token exchange failed: HTTP ${response.status}`)
  return (await response.json()).access_token
}
```

Project API-key access tokens last **60 seconds**. These snippets obtain one token when creating a client. For a long-running server, create a client with a fresh token before expiry and close the old client when its work finishes. Token renewal does not authorize replaying an uncertain write; see [safe retries](/es/guides/errors).

## Schema

```js
// src/schema.js
import { relations } from 'drizzle-orm'
import { index, integer, sqliteTable, text } from 'drizzle-orm/sqlite-core'

export const authors = sqliteTable('authors', {
  id: integer('id').primaryKey(),
  name: text('name').notNull(),
  email: text('email').notNull().unique(),
})

export const posts = sqliteTable('posts', {
  id: integer('id').primaryKey(),
  authorId: integer('author_id').notNull().references(() => authors.id),
  title: text('title').notNull(),
  published: integer('published', { mode: 'boolean' }).notNull().default(false),
}, (t) => [index('posts_author_id_idx').on(t.authorId)])

export const authorsRelations = relations(authors, ({ many }) => ({ posts: many(posts) }))
export const postsRelations = relations(posts, ({ one }) => ({
  author: one(authors, { fields: [posts.authorId], references: [authors.id] }),
}))
```

`.references()` is accepted but not enforced: TerraBase stores a post whose `author_id` matches no author, as SQLite does with foreign keys off.

## Migrations

Generate SQL migrations offline, then apply them from your app or a deploy step:

```js
// drizzle.config.js
import { defineConfig } from 'drizzle-kit'

export default defineConfig({ dialect: 'turso', schema: './src/schema.js', out: './drizzle' })
```

```sh
npx drizzle-kit generate --name init
```

```js
// migrate.mjs
import { migrate } from 'drizzle-orm/libsql/migrator'
import { connect } from './src/db.js'

const db = await connect()
// Table names start with a letter: rename Drizzle's default __drizzle_migrations.
await migrate(db, { migrationsFolder: './drizzle', migrationsTable: 'drizzle_migrations' })
```

Each run applies the migrations it has not applied yet, in one transaction.

`drizzle-kit push`, `pull` and `studio` work too: they read the schema back through the SQLite catalog (`sqlite_master` and the catalog pragmas), which TerraBase answers read-only. Set `tablesFilter` in `drizzle.config.ts` when the database also holds tables Drizzle does not manage. A `push` that would change an existing table needs `ALTER TABLE`, which TerraBase refuses; write that change as a migration that creates a new table instead.

## Queries

```js
import { eq } from 'drizzle-orm'
import { connect } from './src/db.js'
import { authors, posts } from './src/schema.js'

const db = await connect()

const [ada] = await db.insert(authors).values({ name: 'Ada', email: 'ada@example.com' }).returning()
await db.insert(posts).values([
  { authorId: ada.id, title: 'Notes on the Analytical Engine', published: true },
  { authorId: ada.id, title: 'Draft' },
])

// Relational query
const withPosts = await db.query.authors.findFirst({ where: eq(authors.id, ada.id), with: { posts: true } })

// Join
const rows = await db.select({ title: posts.title, author: authors.name })
  .from(posts).innerJoin(authors, eq(posts.authorId, authors.id))

await db.update(posts).set({ published: true }).where(eq(posts.title, 'Draft'))
await db.delete(posts).where(eq(posts.authorId, ada.id))
```

## Transactions

```js
await db.transaction(async (tx) => {
  const [grace] = await tx.insert(authors).values({ name: 'Grace', email: 'grace@example.com' }).returning()
  await tx.update(posts).set({ authorId: grace.id }).where(eq(posts.title, 'Draft'))
})
```

If the callback throws, Drizzle rolls back and nothing is written. Transactions are optimistic: a commit that conflicts with another writer fails with `SQLITE_BUSY`. Retry the whole transaction. A unique violation fails with `SQLITE_CONSTRAINT_UNIQUE` (`error.cause.code`).

## Limits

- `returning()` returns columns, not expressions such as `sql\`upper(name)\``.
- Types follow SQLite: `integer`, `real`, `text`, and booleans and timestamps stored as integers. `blob` columns cannot hold values.
- The [libSQL limits](/es/guides/libsql-clients#limits) apply: reads scan the tables they reference, and a transaction holds at most 256 statements.

</div>
