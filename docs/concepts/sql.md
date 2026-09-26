# SQL

SQL is the only query language. Every collection is a SQL table:

- **Schema-declared fields** become typed columns (extracted at write time).
- The document body is addressable as `body` with SQLite JSON operators
  (`->`, `->>`).
- Metadata is exposed as `_id`, `_rev`, `_hlc`, `_created`, `_updated`.

```sql
SELECT _id, title, body->>'$.tags' AS tags
FROM posts
WHERE authorId = ? AND publishedAt > ?
ORDER BY publishedAt DESC
LIMIT 20
```

Queries are SQLite-dialect SQL, **read-only**: an allow-list of `select`,
`with`, and `explain`. No DDL, DML, attachment, pragma, or non-deterministic
functions. Writes stay document operations (create/update/patch/delete/bulk
through the API and SDKs) — there is no SQL DML, so CRDT semantics, history,
and the billing unit stay exact.

## The live subset

A `SELECT` over one collection with `WHERE` on columns or JSON paths,
`ORDER BY`, `LIMIT`, and FTS5 `MATCH` is **subscribable**: the sync engine
maintains it incrementally and pushes deltas. Joins, aggregates, window
functions, and subqueries are `execute`-only at launch — subscribing to one
returns a typed error with the fix, not a mystery error.

## Where SQL runs

`POST /api/v1/databases/{id}/sql` is the query endpoint (the same statement
submitted to a subscription goes through view attach instead). It returns JSON
or MessagePack, with row/byte/time bounds, and authorization compiles into the
plan — access rules can only reference extracted fields, and saving a rule
auto-creates its index.

## The typed builder

The happy path never concatenates SQL strings. Every SDK ships a typed query
builder over generated schema types that compiles to parameterized SQL; raw
parameterized SQL is always available when you need it.

```ts
import { Direction, select } from '@terrascale/sdk'
import { posts, authorId, publishedAt } from './schema'

const query = select(posts)
  .whereEq(authorId, user.id)
  .orderByField(publishedAt, Direction.Desc)
  .limit(20)
  .subscribe()
```

Here `posts` and its fields are schema definitions. The builder returns a
parameterized subscription descriptor; it does not open a subscription itself.
Pass its SQL and parameters to `TerraScaleSyncV1Client.subscribe` with the
branch's `databaseId`, as shown in the [quickstart](/getting-started/).

## Indexes: automatic, never stale

You can declare indexes, but you rarely need to: the engine watches query
templates (frequency × logical cost), builds candidate indexes invisibly
under a write budget, verifies them in shadow, and only then makes them
visible — journaled so every region follows. Index maintenance happens in the
write's own transaction, so **a visible index can never serve a stale read**.
Queries that would race a building index get a hybrid plan or a typed error
with the fix (retry later, or wait with `terrascale index wait`).

Full-text search is FTS5 `MATCH` with its own auto-index kind.
