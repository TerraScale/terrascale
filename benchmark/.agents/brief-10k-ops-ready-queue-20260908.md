# TerraBase 10k operations/second ready queue

Prepared from the current source and retained benchmark evidence on
2026-09-08. This note is planning/evidence only; no product or benchmark
source is owned here. The user-visible outcome is a fresh, auditable TerraBase
qualification that reaches the agreed 10k operations/second target under the
real public caller, with correctness and durability checks intact.

## Decision required before qualification

Root/Astra must pin what “10k operations/second” means: per workload or the
sum of the four point workloads, single node or RF2, and the latency/error
limits that accompany throughput. The existing family contract gives a usable
qualification envelope (1,000 seeded documents, 10,000 operations per sample,
five samples, concurrency 16, 1,000 warmups), but does not itself set a 10k
throughput threshold. Until this is decided, workers may improve and measure
the existing lane but must report objective status as inconclusive.

## Ready brief A — reduce local origin allocation overhead

**Outcome and caller.** `StoragePort`'s local origin sequence allocator in
`src/storage/sqlite/mod.rs:2472-2481` must produce the same next sequence while
doing bounded indexed aggregates. It is on every local commit and is a
plausible write-lane cost center; the existing indexes are documented at
`mod.rs:951-963,1068-1072,1203-1208`.

**Write ownership.** One worker may write `src/storage/sqlite/mod.rs` and its
focused SQLite tests/evidence only. The benchmark output belongs to
`terrabase/.agents/benchmark-20260907/` and `bench/results/<run>` under
`bench_owner`; do not edit server/API, benchmark drivers, contracts, or
`AGENTS.md`. Shared Cargo builds go through the existing build owner.

**Design.** Replace the raw `MAX` over the three-table `UNION ALL` with one
`MAX` per table and an outer `MAX`, retaining `COALESCE(..., 0) + 1`, tenant and
origin bindings, transaction authority, overflow conversion, and all three
sources. Do not cache a counter or remove a source table. This is the design
already captured in `benchmark-20260907/brief-origin-max-plan.md`.

**Acceptance.** Existing origin allocation and causal-frontier tests must pass;
the result must remain 1 for an empty origin, select the maximum when the
maximum is present in each source table, and remain monotonic across reopen and
backfill. A focused EXPLAIN/VM observation may be retained as engineering
evidence, but only the benchmark owner may produce before/after lane numbers.
Any sequence mismatch, tenant leakage, or failed overflow handling rejects the
change.

## Ready brief B — move synchronous commit blocking to the async boundary

**Outcome and caller.** Public mutation handlers currently call synchronous
`Transaction::commit(LocalDurability::Synced)` (representative callers:
`crates/terrabase-server/src/api.rs:2940,4009,4217,4495` and
`api_schema.rs:101`). The implementation must allow Tokio worker capacity to
continue serving concurrent requests while preserving the same borrowed
transaction ownership and durability result.

**Write ownership.** One worker may write the centralized server helper and
the audited async mutation call sites in `crates/terrabase-server/src/`; its
tests/evidence are also owned by that worker. Do not modify engine transaction
semantics, benchmark scripts/results, contracts, or unrelated background
workers. Root/Astra must approve the final helper scope because it crosses
several handlers.

**Interface/design.** Add a private helper equivalent to
`commit_on_runtime(transaction, durability)`: use `tokio::task::block_in_place`
only on a multi-thread runtime, and call `commit` directly on current-thread
runtime or outside Tokio. This preserves the borrowed `Transaction<'engine>`;
`spawn_blocking` is unsuitable unless ownership is redesigned. Keep operation
reservation lifetime, source barriers, ledger/replay identity, group atomicity,
and error mapping unchanged. Audit every async `.commit(...)` caller before
locking the call-site list.

**Acceptance.** Focused API write, batch replay, durability, conditional
replay, and group-commit selectors must prove success on multi-thread and
current-thread runtimes, no reservation leak on commit/drop, identical retry
receipt, unchanged conflict/uncertain HTTP mapping, pause/revocation fencing,
and batch atomicity. The build owner then runs one source-frozen before/after
write lane; a timing improvement without those checks is insufficient.

## Ready brief C — source-frozen qualification and evidence update

**Outcome and caller.** `bench/scripts/run-terrabase-final.sh` dispatches the
four point workloads and the ingest, local auto-index, and sync families. The
current retained point receipts use one sample/500 operations and cannot meet
the contract. The incomplete write observation (9,880 documents / 8,881
facts) is partial progress, not a result.

**Write ownership.** `bench_owner` owns one release artifact, one bench
artifact, the member/cgroup, and `bench/results/<run>` logs/receipts. The report
owner writes the Markdown report and may update benchmark evidence notes. No
second member, Cargo build, or concurrent run is allowed. The current active
member and shared target described in
`benchmark-20260907/task-table.md` require handoff before use.

**Execution contract.** Use a fresh run directory and verified release
artifact. Run point read/write/query/mixed with 1,000 seed, 10,000 operations,
five samples, concurrency 16, and 1,000 warmups; run single-node ingest at
100,000 documents/sample and batch size 1,000; run local auto-index as a
diagnostic; run sync through `bench/scripts/sync.sh`. Record source manifest,
binary digest, cgroup/resource evidence, command, exit status, raw logs, and
complete receipts. Comparator, RF2, CDN, and cross-region lanes remain
unavailable without their external subjects.

**Known harness correction.** The final script must explicitly export the API
and bearer names consumed by `auto_index_local.rs:29-33` and
`ingest.rs:353-372`; this is the bounded TB-BENCH-2 script task. A preflight
must fail closed when those variables are absent. Do not count setup failures
as throughput or inherit credentials from the operator shell.

**Acceptance and evidence.** Every requested lane completes its required
sample count or is retained as failed/unavailable with the cause. The report
must state throughput, wall time, p50/p95/p99, errors, and family-specific
limits, and must not mix historical engine diagnostics (FTS, restore, ACK,
materializer, auto-index) into served-lane rows. Only after the threshold
decision in this note is made may the report claim the 10k objective.

## Dependencies and return format

Brief A is independent of Brief B. Brief C depends on the corrected source
artifact and harness handoff; it must run after either performance change is
included in the source manifest. Each worker returns changed paths/symbols,
source/artifact hashes, selectors and resource owner, test commands/results,
remaining risks, and the exact evidence path. No worker may claim acceptance;
root/Astra owns the final objective verdict.

## Confirmed recurring lessons

The environment-name mismatch in the final-family script is confirmed by
`benchmark-20260907/recurrence.md`: explicit scoped exports plus preflight are
the correction and prevention. Frozen-snapshot drift also caused compiler
E0407 when an incomplete trait closure was copied; workers must transfer a
complete source manifest and build one coherent snapshot. The earlier
`json_each` hot-path hypothesis is disproven by `evidence.md` and must not
become an optimization task without new measured evidence.
