# Plain-row system records (2026-11-04)

Status: accepted. Storage format 21.

TerraBase databases serve SQL, and functions keep their data in SQL. What still
needs keyed JSON records is TerraBase's own bookkeeping in the system database: API keys, functions and their logs, file
buckets and files, usage, backups, purges, closures and expiry.

## Decision

- Only the system database keeps records: plain rows of `_tb_records (kind, id, version, body)`, unique by kind and id,
  and kinds as rows of `_tb_record_kinds`. A write replaces the row; its version is the commit offset of the transaction
  that wrote it, so versions only grow. A delete removes the row. There is no per-field state, hybrid logical clock,
  origin, tombstone or retained event log. Record operations on a client database throw.
- A client database holds the customer's SQL tables plus `commit_offset`, `_tb_sql_storage` (its SQL storage counter)
  and the shape tables.
- Commits report only their offset; the per-commit change marker had no reader.
- No replica identity file: the write-intent journal tags intents with a random per-process run id, and a clean stop
  clears its own run's intents. Every non-faulting intent a start finds was left by a process that exited uncleanly and
  marks its copy faulted, as before.
- Shape lineage records carry the database incarnation, table generation and table; no replica id.
- Shared-history erasure (`/admin/history-erasures`) and closure cancellation
  (`/admin/projects/{project}/databases/{database}/closure-cancellations/{generation}`) are removed. Both kept their state
  as records inside the auth database, and ts-auth no longer calls either: it keeps auth data, deletion jobs and closure
  cancellation in its own SQL tables. Account erasure remains project closure, database purge and backup key shredding.
- A database id the catalog unregistered is kept in `retired_databases`, replacing the tombstone that refused its adoption.
- Catalog maintenance (purges, closures, expiry, the file audit and retried cleanups) runs in `CatalogMaintenance`. The
  sweep that deleted local files of databases the replicated catalog marked deleted is removed.
- The unused actor router is removed.

Older data directories are refused at start (greenfield, no migration); a production data reset is required.
