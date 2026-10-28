# SQL read cache

- Status: implemented, 2026-10-27. Applies to every relational read path in [relational-sql.md](relational-sql.md):
  the HTTP SQL API, Hrana and the PostgreSQL adapter.

## Why

Before an engine read ran, the statement text was tokenized and parsed with SqlParserCS, validated, guarded, and
rendered and parsed back for the defense-in-depth check. SqlParserCS's visitor finds each node's children by
reflection, on every walk. On beta at concurrency 1, these steps took about half of a point SELECT's server CPU, and the
same work was repeated for every request.

## What is kept

`SqlService` keeps one cache per process, keyed by database:

- **Parse:** the result of a `SELECT`, `VALUES` or `WITH … SELECT` text, as a shared `ReadSql`. Other statements are
  parsed every time.
- **Template:** for a shared read without catalog relations, keyed by text, the JSON of the read tables' definitions,
  `describe` and the `_id` table of an `UPDATE`/`DELETE`. A template is everything `SqlRead.Compile` derives: CTE tables
  and key filter, the statement's own CTEs, the rendered statement, and its extra parameters.
- **Extra parameters:** the coerced copies of compared parameters (SqlCoerce) and a point read's key copy are recorded as
  "parameter n converted by affinity a" (or a literal key). `SqlRead.Bind` computes their values from each statement's
  arguments. A parameter compared with a typed column always gets its copy, so the rendered text never depends on an
  argument. A copy that converts nothing compares exactly as the parameter did.

Arguments are still converted and bound for each statement, and every result is read from the database at that moment.

## Why answers cannot change

- Compiling rewrites a tree in place (row guard, bounded concatenation). A shared tree is never compiled: a miss parses its
  text again privately and compiles that parse with the code that ran before this cache.
- A table definition change (drop and create, a new column) changes the template key. A read of the catalog
  (`sqlite_master`, pragma tables) embeds its rows in CTEs, so it never keeps a template.
- Keys hold the database id, so a template never crosses databases or tenants. Billing, the work budget, scopes and
  QoS are unchanged: they act on the program and its result, not on how it was derived.

## Bounds

At most 4,096 entries and 64 MiB estimated: a tree is estimated at 64 bytes per character of its text, a template at
twice its text plus its tables. No single entry may take more than a sixteenth of the bound. An insert past either bound
empties the cache first.
