# 0018: Reuse Turso where the contracts match (RT7)

- Status: proposed, 2026-10-26. Q-TURSO10 keeps the fast client path on the existing TCP wire protocol.
- Decision: reuse native prepared programs for logical copies first; preserve the logical replication and recovery layers.
- Scope: source audit at TerraScale `8b5227ee8`; measurements in
  [2026-10-26 parity](../../../benchmark/bench/2026-10-26-turso-parity.md).

## Sources and qualification boundary

Both the vendored .NET source and native runtime pin **Turso v0.8.2**, commit
[`5c168be5ef6a32ac966daf5983502a4f694b37ad`](https://github.com/tursodatabase/turso/tree/5c168be5ef6a32ac966daf5983502a4f694b37ad).
The qualified runtime includes FTS, runtime-dispatched AEGIS and TerraScale's typed storage-error and budget patches:
[driver provenance](../../../src/vendor/Turso.Data.Sqlite/UPSTREAM.md),
[native qualification](../../../src/vendor/Turso.Native/README.md),
[reproducible build](../../../scripts/vendor/Dockerfile.turso-native).
All upstream paths below refer to that exact commit, not to current upstream main.
GitHub's latest release on inspection was v0.8.3 (`607bff9fd0e547a25a97b3bf06e83bcc5d99d53c`, 2026-10-12).
Its existence is not evidence that TerraBase's CDC, encryption, recovery or snapshot constraints are fixed.
An upgrade needs native qualification and integration evidence; this work does not upgrade the engine.

## Ranked audit

Rank orders actionable work, then substitutions that need a different contract. Estimates are engineering estimates,
not measured speedups. S = one small PR; M = several focused PRs; L = protocol/native qualification work.

| Rank / capability | What TerraBase does | What pinned Turso offers | Reuse decision; expected win | Risk / effort |
| --- | --- | --- | --- | --- |
| 1. Prepared programs in copies | `TursoCopy.CopyRows` creates, binds and disposes an INSERT per row. | Native prepare/reset/rebind (`sdk-kit/src/capi.rs`); facade `SqliteCommand.ReuseStatement` retains the native program and parameter indexes (our driver patches 0008/0009/0012). | **Yes, now.** One target command per table, parameters created once. Removes per-row compilation, command construction and parameter discovery. Measure rows/s and allocation separately from client parity. | S. Dispose before subsequent DDL; preserve source transaction, rowids, chunks and encryption. Target errors become facade `SqliteException`; copy/rewrite source isolation and the tap's existing operation-level damage recording remain. |
| 2. Prepared programs in PITR replay | `ChangeLog.Replay.ApplyUpsert` rebuilds and prepares INSERT OR REPLACE for each log record. | Same native programs and facade retention. | **Yes, next.** Bounded commands per table/shape; clear on schema records. Expected lower restore CPU/allocation; no hot-read/write gain. | M. Keep record ordering, rowids, damage classification, binding lifetimes and cache byte bounds. |
| 3. Statement caching on client paths | `TursoSession` owns bounded command dictionaries, clears bindings between transactions and invalidates on schema/settings changes. | Native statements and reset, but upstream `TursoCommand.Execute` calls `PrepareCore` every execution; `Prepare()` alone does not retain execution programs. | **Already reused** through facade patches. Keep ownership and bounds; deleting them reintroduces parse/prepare work. | S to extend at proven misses. Turso 0.8.2 can fail a transaction using a statement prepared against an older schema. |
| 4. Prepared plans / schema probe | `TursoSession.CheckSchema` reads schema_version once per transaction before using retained commands; `Settings` and `ForgetSchema` invalidate them. | Native compilation, planning and statistics (`core/translate`, `core/schema.rs`), automatic reprepare with limitations inside explicit transactions. | **Already native plans; no replacement of invalidation now.** Consider a measured writer-owned schema generation only after auditing every DDL/rollback path. Upper estimate from 0015a: 9.8 µs/write for the probe. | M. Removing the probe without a replacement can abort reads/writes after DDL. |
| 5. Capture: native CDC versus tap | `TursoChangeTap` frames committed CDC records into backup segments, tracks holes and schema names, pins snapshots and retains anchors. | `core/cdc.rs`, `cli/manuals/cdc.md`: after-images and transaction commit markers, written in the same transaction. | **Already reuse CDC. Keep adapter.** No second row-change detector exists to delete. Native capture does not provide backup generations, contiguous segments or schema-name replay. | L to replace. Must preserve rollback, crash recovery and empty-table id reuse handling. Never turn capture off for speed. |
| 5a. Captured record decoding | `SqliteRecord.Decode` decodes captured SQLite record bytes with bounded spans before backup framing. | Native `bin_record_json_object` renders record values as JSON (`core/function.rs`, `core/vdbe/execute.rs`). | **Keep the decoder.** The native function cannot format BLOB values in this version; JSON conversion would still require managed parsing. No measured speed benefit; replacing a small span decoder adds transformations. | S to prototype, M to prove complete type/length/error handling. Preserve raw record values and memory admission. |
| 6. Capture: change groups and feed retention | `TursoChangeGroupSource` reads local-origin lattice states from CDC, tracks incarnation/acks/quiet ranges and safe retention; `FeedRetention` expires customer events with cursor floors. | CDC rowids, transaction ids and timestamps; user-directed DELETE of CDC rows. | **No equivalent.** Native CDC is not a seven-day logical feed, authorization proof, origin cursor or per-peer retention policy. Keep these layers. 0015a's feed-sequence consolidation is separate. | L. Deleting retained state too early loses replication/recovery coverage. RT5a owns sender/receiver/acks. |
| 7. MVCC / concurrent writes | One WAL writer per database, bounded group commit; reads use pinned transactions and pooled connections. | `core/mvcc/database/{mod.rs,group_commit.rs}`, `core/mvcc/persistent_storage`: concurrent transactions, logical log and group commit. | **Not safe now.** Could reduce serialized write contention, not demonstrated c=1 cost. Existing spike found interleaved CDC groups can be skipped and CDC is a conflict hotspot. WAL reads already use native isolation. | L, separate protocol proof. Must prove complete capture, FULL durability, LWW and rolling deployment before switching journal mode. |
| 8. WAL shipping / frames | Backup ships logical CDC segments; replication ships logical states. | `core/storage/wal.rs`, `sync/engine/src/wal_session.rs`: native WAL and frame/session machinery. | **No drop-in reuse.** Keep native WAL internally. Turso checkpoints autonomously and ignores wal_autocheckpoint=0; a file tailer can miss frames. Physical pages cannot replace convergent logical states. | L. Native frame access lacks TerraBase's retained-history contract. See measured constraints in backup.md. |
| 9. Checksums / anti-entropy digests | `DocumentStateEncoding.Digest` hashes canonical keyed state; Merkle buckets combine those digests; restore drills compare logical contents. | `core/storage/checksum.rs` verifies 4096-byte physical pages with reserved checksum bytes; AEGIS authenticates encrypted pages (`core/storage/encryption.rs`). | **No substitution.** Native physical authentication is already reused. Nodes can have different physical layouts but equal logical states, and tombstones/counters must participate in anti-entropy. No speed win established from deleting semantic digests. | L for checksum build/format changes; incompatible purpose for anti-entropy. Preserve keyed-state corruption validation. |
| 10. Copy / snapshot | `TursoCopy` reads one pinned view, preserves rowids, omits engine tables, resets peer acks, renews incarnation and commits bounded target chunks. | `core/vdbe/vacuum.rs`; facade `SqliteConnection.BackupDatabase` performs a managed schema/row copy and rejects a facade `SqliteTransaction`. `sqlite_dbpage` writes require cli_only + unsafe_testing. | **Reuse programs, not generic copy.** BackupDatabase is not applicable to the pinned `TursoConnection` source and copies all user tables without lineage cleanup; VACUUM INTO on a non-writer was measured to copy an older view. Generic copies do not implement lineage cleanup. | M/L. Requalify alternatives before deleting the adapter. Decrypted page-cache bytes are not an encrypted portable snapshot. |
| 11. Backup | Continuous snapshots, CDC segments, sealed Tigris objects, retention, PITR and restore drills. | Logical copy/VACUUM and sync pull/push primitives; no matching Tigris backup lifecycle in the pinned engine/driver. | **Keep orchestration.** Native persistence and encryption already underpin it. Reuse 1/2 accelerates snapshot/restore without replacing recovery semantics. | L to replace lifecycle. Must retain contiguous committed cut, object sealing and independent drill evidence. |
| 12. Sync engine | Leaderless lattice/LWW state joins, per-origin cursors, incarnation and peer proofs. | `sync/engine/src/{database_sync_engine.rs,database_tape.rs,server_proto.rs}`; .NET `TursoSyncDatabase` / `TursoSyncBindings` support server-directed replica push/pull. | **No drop-in replacement.** Useful reference and native binding infrastructure; not TerraBase's merge/ack protocol. `DatabaseTape` is Rust-internal, not a callable .NET replay replacement. | L. Central sync semantics would change conflicts, capture, backup format and mixed-version serving. Needs parent coordination before any replication edits. |
| 13. Encryption | `DatabaseKeys` manages per-database envelope keys; backup seals portable objects. | `core/storage/encryption.rs`; `TursoBindings.OpenDatabaseWithEncryption`; AEGIS-256 and typed authentication failures in qualified runtime. | **Already reused for database pages.** Keep key wrapping/rotation and object sealing: they cover distinct boundaries. No managed page cipher to delete. | S for callers, L for a new crypto/runtime qualification. Never materialize plaintext page copies. |
| 14. PostgreSQL / SQL parsing | `Features/Sql` uses SqlParserCS, validates a bounded document grammar and rewrites tenant tables/guards; engine compiles resulting SQL. | `sqlite/parser/src/{lib.rs,ast.rs}` parses SQLite into Rust AST; `postgres/parser/lib.rs` wraps pg_query and its translator. Neither exposes the AST through the shipped C/.NET surface. | **Native execution parser already reused; keep API parsing.** Native prepare cannot replace authorization, memory/work admission, document mutations or dialect translation. Exporting Rust AST would add an ABI and still need those policies. | L for ABI/semantic coverage; no measured parsing bottleneck in document parity. Raw SQL passthrough is unsafe. |

## Correctness and rollout

The first change affects only scratch-copy execution. No stored schema, digest, CDC record, wire frame, public API,
LWW rule, commit sync or ack changes. Old and new nodes can serve together. The source remains a `TursoConnection`
inside the same read transaction; only the private target uses the facade already used by production writers.
The target explicitly creates its scratch file and disables provider pooling so its handle closes before publication.
Bindings must be replaced on every row, including NULL, Unicode and BLOB values, across chunk boundaries.
The target command dies before any later schema changes. Backup, copy, rewrite and restore share this path.

Integration gates: chunked-copy content/rowids/indexes/bookkeeping/cancellation, backup/restore, copy/rewrite source-versus-target
fault isolation, rewrite/rebuild and the existing Fast durability/LWW suite. Slow remains independent and non-blocking.
CI, not a local benchmark, decides merge eligibility. A copy speedup is not evidence that client parity improved.

## Follow-up ownership

**Needs format bump:** 0015a option 1 (0015 option 2), folding the feed sequence into commit_offset, stays with the
parent's batched format lane. It was estimated at about 8 µs/write, under 1%, and cannot alone close medium writes.
Do not edit `StorageFormat.cs` here. Other schema/native/protocol substitutions above are deferred, not approved changes.

For client parity, measure the existing wire path and captured writes on one host with three repeats. Keep admission,
metering, authentication, stored-state validation and FULL sync. Profile the remaining CPU rather than disabling them.
The exact remaining gaps and limits of the evidence belong in the linked measurement report.
