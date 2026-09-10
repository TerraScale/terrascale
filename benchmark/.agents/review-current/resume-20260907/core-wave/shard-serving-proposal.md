# Split serving and replication integration brief

This is a source-backed implementation boundary, not an activation or readiness claim.

## Actual callers and gaps

- `src/member/split_runtime.rs`: `MemberSplitRuntime` has no production constructor caller. `ShardSplitSupervisor` privately opens an `Arc<Engine>` target and releases it on completion. No Member registry retains the target for serving or budgets its lifetime.
- `src/member/mod.rs:DatabaseLease::begin_routed_transaction` and `crates/terrabase-tbep/src/engine_host.rs:EngineHost::begin_routed_transaction` have no callers. Both select a shard token and still begin on the source Engine. `src/engine/shard_split.rs:validate_shard_admission` checks route ownership but not file identity and compares raw document IDs against tokens instead of canonical collection/document keys.
- Public CRUD/history in `crates/terrabase-server/src/api.rs` selects `host.engine()` before snapshots, sessions, conditions, and idempotency. Batch commits one Engine transaction. `list_documents` and `execute_sql` operate one source snapshot. `api_exports.rs` binds export cursor/snapshot to the source Engine identity; `api_usage_indexes.rs` reads source usage. `EngineHost::replay_sync_journal` reads source streams.
- `production_data_runtime.rs:build_peer_databases` rejects duplicate `DatabaseRouteScope`; `build_physical_driver` acquires the one source Engine through `ProductionMemberRuntime::acquire_replication`. `WalAssignment`, `WalReplicaBinding`, `ScopedWalBatch`, `ScopedWalAck`, snapshot addressing, causal driver scope, peer registry admission, and `ReplicationProgress` all identify a database without a shard ID. A target under the same scope would alias source receipts and could acknowledge the wrong file.

## Required interfaces before activation

1. Introduce a producer-owned shard lane identity `(tenant, project, database, database_incarnation, shard_id)` across signed split grants and physical/causal envelopes, snapshot binding, transport dispatch, peer admission, progress, and durability waits. Logical database identity remains customer-facing. Bind target writer/follower workload identities, accepted placement epoch/generation, and exact baseline digest. An observed empty target prefix must remain distinguishable from unobserved zero; source RF2 evidence cannot qualify a target.
2. Add an admitted Member target handle registry keyed by incarnation/shard. Registry insertion must validate signed grant, exact target identity, existing durable storage, immutable residency, and measured target replication evidence. The split worker and replication drivers must share that exact handle. Target handles retain Member admission permits and consume resident-engine/RSS/SQLite budgets; drain, eviction, restore, backup, deletion, grant revocation, and restart recovery include them.
3. Replace source-only serving with a logical database handle carrying the authoritative route generation and admitted engines. Select canonical collection/document key before scope, snapshot, session, conditions, operation replay, and write budget. Recheck file identity and route generation under the commit barrier. Never infer the source file shard from route ordering. Target scope must come from its Engine, not reuse a source `TenantScope`.
4. Implement database-wide consumers before route publication. SQL ordering/limits/aggregates and joins cannot be implemented by concatenating shard results. List/export/history need a stable common boundary and range-aware cursor; sessions need preserved causal lineage and baseline translation; reactive views need ordered source/target publication ownership with no lost/duplicate effects. Preserve existing batch atomicity across moved ranges or obtain a product decision before changing it. Usage aggregation must transfer logical ownership without duplicate storage or write charges.
5. Wire production start/resume/bounded stepping only after the target registry, retained baseline importer, transport lanes, actual consumer callers, and signed serving receipt are connected. Cutover must remain gated on all of these; registration or healthy processes alone is insufficient. Retire source range physically only after reader/session/export/durability obligations clear, without publishing customer deletes.

## Disjoint implementation batches

- Transport/producer: shard lane type, wire vectors and version, signed grant/receipt binding, WAL/causal dispatch, snapshot targeting.
- Runtime: registry/lifecycle/budgets, real target handle acquisition, RF2/causal worker startup, target-only measured progress and durability wait.
- Engine safety: durable file shard identity and canonical-key token verification. This bounded fix can prevent writing a moved key to the wrong source Engine without activating target serving.
- Serving consumers: point operations and sessions; separately database-wide SQL/batch/export/reactive/usage orchestration after consistency interfaces are agreed.

The full retained baseline/revision work is described in `shard-copy-proposal.md`. No whole-engine copy shim, empty proof, or source-scope replication alias satisfies these interfaces.
