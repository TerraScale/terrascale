# TerraBase SQLite backup model

TerraBase follows Litestream's safety model while keeping the backup worker in
the engine. SQLite is accessed only through its API; snapshots use the online
backup API and WAL data is copied as complete frames. A WAL salt change starts
a new generation, and a segment never crosses generations. Partial files are
written with a `.partial` suffix, flushed, and atomically renamed before their
SHA-256 manifest is published.

The backup stream is independent of write acknowledgement and can be resumed
from `(generation, offset)`. A snapshot manifest records the publication
journal sequence so a restore can identify the exact database cut. Restore
verifies the manifest hash before publishing a destination file. Retention is
bounded by `SqliteBackupOptions.RetainedSnapshots`; object-store lifecycle and
provider redundancy remain deployment concerns.

This follows the public [Litestream repository](https://github.com/benbjohnson/litestream)
and its [replication reference](https://litestream.io/reference/): SQLite API
access, WAL generations identified by salts, append-only WAL ranges, resumable
offsets, and verified snapshots. TerraBase keeps these mechanics in-process so
one bounded worker can share scheduling, encryption, and health state across
database files.

Replication keeps canonical UTF-8 bodies as bytes. Clean and dominated
operations do not parse JSON. Only concurrent operations parse the two bodies
needed for deterministic field merge, then retain the resulting state for
future operations. Causal contexts are checked before apply, and tombstones
win concurrent updates to prevent resurrection.
