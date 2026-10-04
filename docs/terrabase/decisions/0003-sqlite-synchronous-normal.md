# 0003: SQLite `synchronous = NORMAL` in WAL mode

- Status: accepted (human decision, 2026-10-04)
- Scope: `src/TerraScale.TerraBase/Engine/Sqlite` (every connection: pooled sessions and the
  connection that creates a database)

## Context

Every database is a SQLite file in WAL mode (0002). With `synchronous = FULL`, SQLite fsyncs the
WAL on every commit, so a write transaction costs one fsync and single-document writes are
bound by disk sync latency (about 4.7 ms each on the benchmark machine). With `NORMAL`, SQLite
syncs the WAL only before a checkpoint and the database file after it; commits in between stay
in the OS page cache.

## Decision

Every connection uses `PRAGMA synchronous = NORMAL`, for write throughput.

## Consequences

- A committed transaction, including a `PUT` or `DELETE` that the API already acknowledged, can
  be lost after a power loss or an operating-system crash. What is lost is the transactions
  committed since the WAL was last synced, which happens at the latest at the next checkpoint
  (about 1000 pages of WAL by default). The database rolls back to the last synced commit.
- A crash of the TerraBase process alone loses nothing. Committed data is already in the OS page
  cache, and SQLite recovers it from the WAL on the next open.
- No corruption: WAL frames are checksummed, and recovery ignores frames that were not synced
  completely.
- TerraBase does not claim that an acknowledged write survives power loss on a single node. Any
  such guarantee has to come from replication or a later decision, with evidence.
- Measured effect: see the benchmark in `docs/sqlite-query-plans.md`.
