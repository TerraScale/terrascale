# Shard origin-lane and multi-shard replay brief

Status: read-only design brief for Astra's P1 finding. No product code or tests were changed.

## Confirmed collision

`ScopedShardEngine::open_target_with_mode` in `src/engine/shard_split.rs` creates a target Engine with `with_replication_origin(origin)` where `origin` is only the parent Region (around lines 890–900). The target SQLite file therefore allocates its own `origin_seq = 1` under the same origin string as the source file. Source and target can concurrently publish `(tenant, origin=GRU, origin_seq=1)`.

`FileShardIdentity` already persists `origin_lane_incarnation` and the route identity codec carries it through `encode_file_identity`/`decode_file_identity` (around `src/engine/shard_split.rs:731-765`). That field is identity metadata only; it is not used as the Engine replication origin. Reopen and restart therefore retain the collision.

## Identity paths that must converge

Use the persisted lane incarnation at every producer and consumer boundary:

| Path | Required change |
| --- | --- |
| Engine origin (`EngineConfig::with_replication_origin`, `StoragePort::local_replication_origin`) | Accept the canonical lane origin persisted with file identity. Region remains placement metadata. |
| Local publication (`ReplicatedPublication.origin`, `JournalProvenance.origin`, SQLite `replication_publications.origin`) | Store and compare the lane origin token. |
| Wire publication (`replication_wire.rs`) | Encode/decode the canonical token and reject malformed values. |
| Causal lane (`CausalDriver::origin`/`local_origin`) | Bind frame prefixes/frontiers to lane origin; reject Region-only aliases. |
| Repair (`JournalRowId::new(origin, origin_sequence)`) | Use lane origin and verify it against authenticated assignment/file identity. |
| Sessions (`SessionFrontier` currently keyed by `RegionId`) | Key by canonical lane origin; bump token version and reject old Region-only tokens. |
| Protobuf/OpenAPI frontier/checkpoint fields | Carry lane origin with tenant/database, epoch, and placement binding. |

Do not identify a target by spoofing another Region. Derive one strict origin token from `FileShardIdentity`: Region plus nonzero `origin_lane_incarnation` (and database/shard identity where storage requires it). Use its canonical bytes/string in maps, fingerprints, causal context, repair IDs, and idempotency bindings. Mint it once at shard admission, persist it before writes, and reuse it through reopen, failover, restore, and bootstrap. Missing or mismatched identity fences the file.

## Source-only reactive replay defect

`EngineHost::replay_sync_journal` in `crates/terrabase-tbep/src/member_host.rs` calls `self.engine.replay_sync_journal(scope, ...)` once. That API walks `Engine::tenant_commits` in one SQLite file (`src/streaming/backbone.rs:456` onward). After split cutover, target-file writes are absent from the source journal, so source-bound sync/reactive callers miss them. A scalar `ChangeCheckpoint` cannot represent progress in two independent files.

The serving owner must load the immutable route and lane identities, maintain one checkpoint per lane origin/incarnation bound to route generation and database epoch, replay each lane, and merge by deterministic `(causal stamp, lane origin, lane sequence, write index)`. Return an opaque vector checkpoint containing all cursors, route/database identity, selector, tenant, and authorization binding. Unknown or removed lanes must produce an explicit gap/resync outcome; they must never appear drained. Reactive delivery needs the same coordinator, with authorization and tenant checks before fanout and global ordering/limits merged centrally.

## Qualification ownership

The production member/EngineHost integration owner should create one database with source and target lanes in one Region, commit one write on each, and run the real sync/reactive path through cutover and restart. Prove distinct origins, exactly-once convergence after reorder, target visibility after cutover, bounded gap/resync on cancellation or missing lane, stale checkpoint rejection, and tenant authorization before fanout. Storage owns the durable lane-origin/vector-checkpoint codec; replication owns causal/repair wire checks; reactive owns global merge/subscriber assertions.
