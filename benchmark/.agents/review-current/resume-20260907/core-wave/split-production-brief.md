# Production split integration brief

Source review 2026-09-07. This is incomplete launch work, not qualification evidence.

## Cleanup trace

`src/member/mod.rs` exports `MemberSplitRuntime`; `src/member/split_runtime.rs` owns bounded workers; `src/engine/shard_supervisor.rs` calls snapshot/catch-up/durable cutover implementations; `src/engine/shard_split.rs` persists state and target identity. These are useful unfinished implementation, not confirmed obsolete code. No independent deletion established.

## Current executable chain and breaks

1. Production server runtime composes `ProductionMemberRuntime` and actual peer drivers. Its acquired lease resolves tenant/project/database through one member engine. No production `MemberSplitRuntime` constructor or concrete `SplitAdmissionAuthority`/`SplitOrchestratorAdapter` implementation exists.
2. Split runtime requires a previously persisted router. `install_shard_route_catalog` is currently called only by engine tests. Introduce an idempotent initial catalog operation for a genuinely admitted database, independent of a synthetic split record. It must refuse conflicting existing identities and preserve restart generation.
3. Worker target creation opens a sibling encrypted engine (`ScopedShardEngine::open_target_with_mode`), copying owner, encryption lease and origin. It does not compose target replication/placement. Its source cleanup adapter currently returns success without deleting the moved range.
4. Existing `DatabaseLease::begin_routed_transaction` and `EngineHost::begin_routed_transaction` compute a shard token but always call `self.engine.begin`. There are no production callers. A token cannot dispatch storage. Canonical collection+document keys must match commit admission (engine recovery lane owns the identified raw-ID mismatch).
5. RF2 binds `DatabaseRouteScope` (tenant/project/database only). `ProductionMemberRuntime.wal_bindings` and WAL assignments use that scope. `ReplicationPeers::add_databases` rejects duplicate `(peer, scope)`, and observations/receipts are similarly scoped. A second physical SQLite file cannot reuse the current lane identity.

## Independently implementable boundaries

| Boundary | Owner files | Implementation and pass condition |
| --- | --- | --- |
| Durable initial route | engine shard catalog/member | Idempotent admitted root-route initialization, correct existing/restart behavior, conflicting identity rejection; no raw metadata edit in test setup. |
| Plan integrity | src/shard_split/runtime.rs | Reject copy lower bound different from source route and split key outside source before persisting/creating target. Implemented alongside persistence retry correction; test queued. |
| Control authority | TBEP placement control + orchestrator producer | Immutable durable plan binds authenticated scope/incarnation, operation ID, source/target, canonical bounds, placement epoch, exact writer/follower identities. Resume reads same accepted plan; revoked/stale authority cannot authorize infrastructure change. |
| Physical shard identity | cluster WAL wire/assignment/driver + server peers | Add explicit physical-shard identity consistently to wire, snapshots, ACKs, proofs, admission maps and paths, preserving public customer database identity. Prove two concurrent files cannot accept each other's receipts and restart resolves each file. |
| Serving dispatch | member + EngineHost + API | Lease selected physical engine using authoritative durable generation; commit rechecks authority at same fence boundary. Point reads/writes before/during/after cutover and restart must hit correct engine. Remove dormant source-only helpers when replaced. |
| Query/reactive/control fanout | query/public API + member | Execute collection/global query semantics with global ordering/limits/aggregates; enforce authorization before distributed work. Schema, TTL, metering, idempotency, sessions, export/backup and reactive publication must remain one customer database experience. |
| Scheduler | server runtime + member split runtime | Resume accepted operations at database admission; bounded demand samples and pages; bind target RF2 readiness to cutover; publish durable route/frontier; shutdown preserves operation. Added capacity must become reachable serving capacity. |
| Cleanup | engine snapshot/history + supervisor | After verified target and serving cutover, remove moved source data without customer delete billing, double-counted storage, lost retained history or invalid backup recovery. Preserve source while any verification/receipt outcome is unknown. |

## Consequential implementation decisions

An explicit physical shard subidentity is needed somewhere in the existing database-only RF2 protocol. Reusing the same scope is rejected today and would conflate receipts if that rejection were merely removed. Treating a target as a new public database would incorrectly change customer identity, quotas and billing. Proposed direction: physical identity nested beneath unchanged customer database scope, carried end-to-end through internal transports.

Cross-shard query and batch behavior cannot be implemented by concatenating individual engine results: global ORDER BY/LIMIT/aggregation, snapshot/session frontiers and operation idempotency need a coordinator. Existing transaction atomicity promises must be traced before selecting partitioned commit behavior; do not silently weaken them.

Cutover must not be enabled as soon as local copy succeeds. Require admitted target placement and measured target RF2 durability, plus functioning target serving dispatch. The existing no-token rejection once a shard catalog exists makes scheduler-only wiring actively disruptive to normal API writes.

## Verification selectors

No Cargo run by this worker. `tb_checks` owns `cargo test -p terrabase --lib shard_split::runtime::tests` at lowered priority. Required connected qualification additionally exercises actual control transport and public API with acknowledged-write ledger, both source/target RF2 followers, process restart at each phase, placement changes, lost responses and added-capacity traffic distribution.
