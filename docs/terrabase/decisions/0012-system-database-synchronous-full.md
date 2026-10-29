# 0012: The system database syncs every commit (`synchronous = FULL`)

- Status: accepted (COS decision, 2026-10-12, option B)
- Scope: `src/TerraBase/Engine/Sqlite` (`SqliteDatabaseStore.SyncsEveryCommit`, set on `_system` only). Amends
  [0003](0003-sqlite-synchronous-normal.md) for the system database; tenant databases keep 0003.

## Context

`_system.db` holds what authenticates and authorizes every request on a node: organizations, projects, database
registrations, API keys and their revocations
([0005](0005-multi-tenancy.md)). Under 0003 its commits, like every other, stayed in
the OS page cache until a checkpoint synced them.

The write-ahead log fault shim (`tests/TerraBase.E2E/WalFaults`, H4) simulated a power loss on a region: every log cut
back to its length at its last sync. `_system.db-wal` had synced only its 32-byte header; the 160 KB written after it
were gone. After the restart the region answered `401 invalid_key` for the database it had registered: the organization,
project and database it had acknowledged no longer existed there. An API key, a revocation or a membership acknowledged
just before the power loss would be lost the same way, and a lost revocation re-enables a key the operator revoked.

## Decision

Every write transaction of the system database runs with `synchronous = FULL`, the way a durable write transaction does
(`BeginDurableTransactionAsync`): its commit returns after the write-ahead log is synced, and the pooled session goes back
to `NORMAL` afterwards. That covers plain and durable transactions, batched writes and catalog replica transactions.
Work on the writer outside a transaction (snapshots, copies, change-capture cleanup) is not covered: it acknowledges no
write, and a cleanup lost to a power loss runs again.

Tenant databases kept 0003's `NORMAL` until 2026-10-13. Since #181, every write transaction of every database syncs the
same way (0003 as amended).

## Consequences

- An acknowledged system database write (an API key, a revocation, a membership, an organization, a project, a database
  registration, a management commit and its record) survives a TerraBase process crash, an OS crash and a power loss on
  its node. This backs the fail-closed restore goal: a node never comes back with keys or revocations it acknowledged
  missing. Proof: `SystemDatabasePowerLossTests` (fails with `401` on the previous commit, passes with this one).
- Each system database write costs one WAL fsync: catalog writes are low-volume (one per API key, one per management
  commit), so throughput does not matter there; their latency is measured in
  `benchmark/bench/2026-10-12-system-synchronous-full.md`. Tenant writes are unchanged.
- **Tenant databases (0003, unchanged).** An OS crash or a power loss can lose the commits a tenant database acknowledged
  since its write-ahead log was last synced, which happens at the latest at its next checkpoint (about 1000 pages of
  log by default): documents written, changed or deleted, and the collection, index and schema changes stored in the
  database. The file is not corrupted: the database rolls back to its last synced commit. A process crash alone loses
  nothing. Writes that already reached a peer come back through anti-entropy; writes that never left the node are lost.
  Durable write transactions and replicated applies (`synchronous = FULL`) are the exceptions.
- Not covered by this decision: torn writes of the main database file after a checkpoint, and a lost or corrupted volume
  (refill from peers, backups).
