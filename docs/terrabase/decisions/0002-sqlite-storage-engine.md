# 0002: SQLite storage engine behind engine interfaces

- Status: accepted (human decision, 2026-10-04)
- Scope: `src/TerraScale.TerraBase/Engine`

## Context

The repository rules left the C# storage substrate open; the Rust reference uses a pinned
Turso engine. TerraBase is a database-as-a-service: clients store JSON documents.

## Decision

- TerraBase stores JSON documents in **SQLite** through `Microsoft.Data.Sqlite`, one database
  file per TerraBase database (`<data-directory>/<name>.db`; [0005](0005-multi-tenancy.md)
  supersedes this layout with one directory per tenant), WAL journal mode.
- Every caller uses the interfaces in `Engine/`: `IStorageEngine` (open and list databases),
  `IDatabaseStore` (transactions) and `IStorageTransaction` (document reads and writes).
  `Engine/Sqlite/` is the only code that references SQLite.
- Schema v1 is one `documents (collection, id, version, body)` table, `WITHOUT ROWID`, with
  `PRAGMA user_version = 1`. A file with a newer version is refused.
- TerraBase's own state (API key hashes) lives in the reserved `_system` database, which clients
  cannot name. It stays at `<data-directory>/_system.db` under 0005.

## Consequences

- Another engine (for example Turso) is a new `Engine/<Name>/` implementation of the same
  interfaces, chosen with human approval.
- Microsoft.Data.Sqlite runs its async APIs synchronously, so disk work currently runs on
  request threads. Move it to a dedicated scheduler when write latency under load matters.
- Durability depends on SQLite's WAL and `synchronous` settings and has not been measured or
  qualified. Do not claim a durability guarantee from this decision alone.
- The Rust reference's distributed document state (signed v2, object-durable profiles) is not
  ported. Replication still has to define how committed SQLite transactions become replicated
  events.
