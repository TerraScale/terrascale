# Shard supervisor correction cohort

The real member wrapper calls the engine supervisor, but no production entrypoint instantiates MemberSplitRuntime. This cohort fixes that existing supervisor's observed failures; it does not qualify production shard serving.

## Corrected behavior

- CutOver/Complete recovery retries frontier publication and finalization before stale copy/catchup workers.
- Target staging inherits an admitted open source writer epoch; owned targets otherwise start fenced at epoch zero. Existing target opens forbid creation.
- Before route CAS, real source/target canonical document/version range measurements are compared and the measured target facts are synced into a split-bound receipt. Completion checks the durable measurement, preserving legitimate post-cutover target writes. Retry reuses the existing proof boundary.
- Route CAS persists the actual drained source frontier. Empty snapshot pages do not issue empty transactions.
- Final catchup progress is committed with routing; partial checkpoint writes are amortized so one-group pages do not chase their own journal forever. Each cutover attempt gets a fresh fence permit.
- Catchup measures the publication boundary, not the document-only history maximum. An empty actual publication tail before the boundary fails closed.
- Source shard routing/lifecycle/target identity/proof controls are excluded from migrated private payloads; schema and other private controls remain. Copying source routes previously rejected subsequent target replay.

## Verification

Designated rust_checks worker ran:

- cohort17-supervisor.log: 2 passed (crash/publication retry/post-cutover write; one-group checkpoint-tail completion).
- cohort18-filter.log: 1 passed (canonical range and private control preservation).
- cohort12-verification.log: 1 passed (missing/empty target refused without recreation).
- cohort13-runtime.log: 4 passed (generic runtime recovery/publication ordering).

Logs: /tmp/terrabase-rust-checks-20260907/. Formatting and git diff --check passed. No Cargo run by the implementation worker. No commit/push performed by this worker.

## Source hashes

| File | SHA-256 |
| --- | --- |
| src/engine/shard_split.rs | b09c6d5230ede83677caf91a314f53edfa1d33f11ce1e15a03cfdcb2bbcecf91 |
| src/engine/shard_split_tests.rs | a5ceec83f15f17488962cfedd11fa3ca13b96bb9e8f27c70d512d53a12aaf995 |
| src/engine/shard_supervisor.rs | c981bc7a5723744884032a8a51ef8a68acf90df0738230203bc8990325c119c5 |
| src/engine/shard_catchup.rs | e2f6ee825de5761f1a37c9ee303596f057feb3320825499882e592726af0d380 |
| src/shard_split/runtime.rs | 13983802072eaf7a915f4315061c1735784c26e126cb2b3f616adfb618a42f53 |
| src/storage/mod.rs | a7befaed857587a642c25cf1540f24dba65e7bab9e9db6aa90029abbba92ed93 |

## Remaining source-backed gaps

1. Snapshot completion persists target_applied_through at the snapshot boundary, while the first fresh journal worker starts at zero. Resume starts from the persisted cursor and can skip required original history replay. Next bounded fix: persist actual journal cursor until replay progresses, with restart between snapshot completion and first journal page.
2. Snapshot target tx.put invents target-local versions/causal provenance. Original public revisions also include storage sequence, so end-state body/version comparison alone does not prove revision/session continuity. Need preserved original history/provenance plus public revision semantics before activation.
3. Missing interior/start publication history is not fully proven by the empty-tail guard. Sequence discontinuities alone are not a general proof because storage sequences may have legitimate holes. Need retained baseline/floor and original-publication summary proof.
4. No authenticated split-specific accepted grant, target RF2/causal registration, or production MemberSplitRuntime instantiation exists. ProductionAdmissionRuntime owns the parent Member and PlacementJournal, but parent placement acceptance is not target shard durability proof.
5. DatabaseLease::begin_routed_transaction selects a shard token but still opens the parent Engine. Reads/writes/query/history/export/reactive paths need actual target resolution and multi-shard semantics.
6. cleanup_split_source remains a no-op. Ordinary tombstone writes would replicate deletion into the target and are not a safe substitute for physical range retirement.

These remain required work. Do not enable automatic route cutover or claim shard-serving readiness from this cohort.
