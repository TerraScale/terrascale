# Leftover branch decisions, 2026-10-29

Audit of all 194 refs in the cleanup list against main `4bacd8a818cff3fab76f3f0558bdc4585361e617` (#877). Every row identifies the original branch tip so decisions remain traceable after ref deletion. `ported` can mean only the still-wanted subset; the reason states what was preserved or excluded. `superseded` includes intentionally rejected/replaced designs and frozen benchmark stacks. No performance, interoperability or qualification success is inferred from a source port; the folded PR must pass current CI and review.

Rejected claims/hash candidates (#814/#816), cached-open concerns (#836), unresolved CORS tail concerns and unlaunched auth experiment holds remain closed/held. No StorageFormat change, migration or retired SQLite engine is introduced. Auth changes require current security review; hosted form previews must be attached for the exact final PR head before merge.

The local recovery bundle `~/Backups/merge-back-20261029/terrascale-194-branches.bundle` preserves all source refs. Delete source refs only after the folded PR merges, excluding any current open-PR branch and any branch owned by another author. The initial historical-PR audit found only MarioGK among listed branch PR authors; refresh ownership and open PRs before deletion.

Additional local-only salvage: beta's `feat/postgres-hastables-slice` at
`f8fec2d6effb92b3a6eefb894bc372e06b653628` was recovered from its worktree cleanup
bundle. Its savepoint and shared PostgreSQL test-client work overlaps main and
the folded port. Retain only its newer `db34d59e3` aborted row-limited Execute
refusal and `f8fec2d6e` typed message regression; use main's existing shared
`PostgresWireClient` and `PostgresSql` instead of duplicate test clients. This
local-only ref is additional to the 194 remote refs below.

Its separately saved uncommitted patch
`postgres-hastables-slice-f8fec2d6e.patch` contributes the exact Npgsql EF
table-existence query, executed through the real SQL session, and additional
savepoint bounds/identifier tests. Preserve main's known catalog relation set so
ordinary `pg_orders` tables remain SQL. Add listener coverage for transaction,
savepoint and prepared-portal visibility and strict predicate matching; do not
claim general EF compatibility from this single introspection operation.

Additional local-only salvage: beta's `fix/client-sql-libsql-cors` at
`1d99fa8252c15b8816e20440f230f6898670b297` adds bearer-only CORS for exact SQL
execute and libSQL HTTP routes. Port it with main's public probe policy and
listener boundaries intact. This is a security-sensitive surface expansion and
remains on hold until security review explicitly approves it. It is independent
of the rejected/unresolved CORS span-matcher performance experiment; that runtime
matcher remains excluded.

Additional local-only salvage: beta's `fix/sql-http-boundary-coverage` at
`f9bbc5be4c4a0558ed49d00e87af2106bd0cb0a0` contributes 16 real HTTP SQL/Hrana
boundary cases: exact/over-limit fixed and chunked bodies, statement/request
counts, row/change-group limits, rollback and recovery. Port the tests, without
the dated qualification report; the folded PR's current CI must qualify them.

| Additional salvage source | Decision | Hold / evidence |
| --- | --- | --- |
| delta `fix-dependency-advisories-3086c1c81.bundle` (native upgrade) | held, not ported | Publication, ARM64 and storage-format holds remain. Source bundle stays in `~/Backups/worktree-salvage-20261029/terrascale/`; no upgrade or activation in this PR. |

Decisions: 45 already in main, 23 dropped evidence, 17 ported, 109 superseded.

| Branch | Original tip | Decision | Evidence / retained behavior |
| --- | --- | --- | --- |
| `bench/api-cors-span-report` | `3bd231513731` | dropped evidence | Latest tail recheck 1352e2bfc remains UNRESOLVED: regression not excluded. Do not ship span matcher. Shared external CPU observer retained once. |
| `bench/cached-system-open-r1-report` | `6c3bd20d828c` | superseded | Cached-open runtime remains held for CPU/p50/p99 concerns recorded by merged #836; no activation. Shared external CPU observer retained once. |
| `feat/auth-oidc-mfa-hardening` | `ec6f83bfc12d` | ported | Remaining OIDC replay/retention, consent, continuation, signup resend and accessible hosted forms; integrated with current durable outbox, lineage, rate limits and #882 copy. No retired credential binding restored. |
| `feat/bench-sdk-http2-pin` | `d49d18055026` | already in main | Measured HTTP/2 SDK pin superseded by current sibling SDK ProjectReference and merged #820. |
| `feat/perf-suite-runner` | `f7380eec30a3` | already in main | Unified runner and baseline comparison evolved in #775/#819; current benchmark/scripts/perf-suite.sh retained. |
| `feat/postgres-orm-compat` | `91188f7639d8` | ported | Bounded savepoints including 91188f763 aborted portal/Describe fixes; retained current billing, deadlines and revocation. |
| `feat/r1-capture-benchmark` | `669e95d7f64a` | superseded | R1 capture findings/runtime merged #370. Standalone harness assumes pre-encryption .db layout and private frontier tables; not a current supported benchmark. Current FeedCursorBench/repl-local remain. |
| `feat/r4-ack-benchmark` | `8e15fa475b23` | superseded | Durability findings and focused RemoteAckBench already integrated by #381; dated duplicate reports omitted. |
| `feat/r4-durability-report` | `3853f95200f1` | superseded | Durability findings and focused RemoteAckBench already integrated by #381; dated duplicate reports omitted. |
| `feat/r7-bench-after-r0` | `889186a3f797` | superseded | Current internal/repllocal harness #367 replaces flat command helpers; R2 envelope/cursor #376, R3 capture #370, R5 #364/#782, R7 #384 and R9 #399 already integrated. Frozen comparison baselines omitted. |
| `feat/r7-bench-before-r0` | `5ad2e0cec5c2` | superseded | Current internal/repllocal harness #367 replaces flat command helpers; R2 envelope/cursor #376, R3 capture #370, R5 #364/#782, R7 #384 and R9 #399 already integrated. Frozen comparison baselines omitted. |
| `feat/r8-fault-soak-cluster` | `c7125fecc333` | already in main | Seeded multi-process fault soak integrated #375; subsequent durability recovery coverage #562 retained. |
| `feat/r8-fault-soak-feeds` | `2931c4ac097e` | already in main | Seeded multi-process fault soak integrated #375; subsequent durability recovery coverage #562 retained. |
| `feat/r8-fault-soak-oracles` | `09a7fa57a71e` | already in main | Seeded multi-process fault soak integrated #375; subsequent durability recovery coverage #562 retained. |
| `feat/r8-replication-fault-soak` | `bca751864d0d` | already in main | Seeded multi-process fault soak integrated #375; subsequent durability recovery coverage #562 retained. |
| `feat/repl-benchmark-integrated` | `98112a551931` | superseded | Current internal/repllocal harness #367 replaces flat command helpers; R2 envelope/cursor #376, R3 capture #370, R5 #364/#782, R7 #384 and R9 #399 already integrated. Frozen comparison baselines omitted. |
| `feat/repl-benchmark-r0` | `7a96bf45691d` | superseded | Current internal/repllocal harness #367 replaces flat command helpers; R2 envelope/cursor #376, R3 capture #370, R5 #364/#782, R7 #384 and R9 #399 already integrated. Frozen comparison baselines omitted. |
| `feat/rt6-endpoint-lww` | `943ce873c1bc` | ported | Cross-endpoint clock-skew LWW qualification, adapted to current SDK reference. |
| `feat/rt6-region-guidance` | `b68771eda4a9` | ported | Socket pins only; current HTTP guidance #810 and live key revocation take precedence. |
| `feat/rt6-sql-add-column` | `7af0dbaad8a9` | ported | Nullable ALTER ADD COLUMN and integration coverage; retained full input validation. |
| `feat/rt6-sql-hrana-routing` | `dfddb616b7c1` | ported | SQL/Hrana convergence and issuer failover qualification, using #810 HTTP refusal contract. |
| `feat/ts994-orphan-db-sweeper` | `8082fd8ff21e` | superseded | Current durable orphan deletion #766 and abandoned creation #656 replace these earlier implementations; no old sweeper resurrected. |
| `feat/ts994-orphan-files-delete` | `5347d94c5569` | superseded | Current durable orphan deletion #766 and abandoned creation #656 replace these earlier implementations; no old sweeper resurrected. |
| `feat/ts994-orphan-files-dry-run` | `f9c9718f57a4` | already in main | Durable orphan audit integrated #663 and refined #771; current sweeper #766 retained. |
| `fix/api-auth-temporary-allocation-ab` | `aa325181b89c` | dropped evidence | Combined auth candidate was preregistered, not launched; requires separate approval/source review and does not reopen #814/#816. Shared CPU observer retained once. |
| `fix/api-auth-temporary-allocation-ab-tools` | `caee662b149c` | dropped evidence | Combined auth candidate was preregistered, not launched; requires separate approval/source review and does not reopen #814/#816. Shared CPU observer retained once. |
| `fix/api-claims-materialization` | `984bc40c96d4` | superseded | Candidate rejected by merged #814; do not reopen claim-materialization decision. |
| `fix/api-claims-validation` | `4e3b9a40dce1` | superseded | Candidate rejected by merged #814; do not reopen claim-materialization decision. |
| `fix/api-common-overhead-evidence` | `944447a26bd5` | dropped evidence | Historical observations/measurement audit only; no missing product implementation or reusable tool to port. Original commit retained in branch audit/bundle. |
| `fix/api-common-overhead-profile` | `c400ebc63a77` | ported | Reusable api-cpu-observer.py retained with usage/limitations. Frozen-binary collector, run-tree-specific trace analyzer and dated report omitted. |
| `fix/api-cors-span-path` | `f18d8f4b1030` | dropped evidence | Latest tail recheck 1352e2bfc remains UNRESOLVED: regression not excluded. Do not ship span matcher. Shared external CPU observer retained once. |
| `fix/api-cors-span-report` | `ec4a78534091` | dropped evidence | Latest tail recheck 1352e2bfc remains UNRESOLVED: regression not excluded. Do not ship span matcher. Shared external CPU observer retained once. |
| `fix/api-cors-span-tail-recheck` | `9cbb2126b9ea` | dropped evidence | Latest tail recheck 1352e2bfc remains UNRESOLVED: regression not excluded. Do not ship span matcher. Shared external CPU observer retained once. |
| `fix/api-create-stage-qualification` | `ef3e45e223a8` | ported | Latest functional stage metrics and real HTTP abort controls through ed061c940; dated report omitted. |
| `fix/api-dotnet-parity-cell-gates` | `de959fbf8571` | superseded | Newest complete split runner fix/api-dotnet-parity-run-lifecycle ported once; existing main document/tail controls #823/#828/#831 retained. |
| `fix/api-dotnet-parity-cells` | `e4a33fc10541` | superseded | Newest complete split runner fix/api-dotnet-parity-run-lifecycle ported once; existing main document/tail controls #823/#828/#831 retained. |
| `fix/api-dotnet-parity-lanes` | `2d47625e7b37` | superseded | Newest complete split runner fix/api-dotnet-parity-run-lifecycle ported once; existing main document/tail controls #823/#828/#831 retained. |
| `fix/api-dotnet-parity-lifecycle` | `9b62c7a26d36` | superseded | Newest complete split runner fix/api-dotnet-parity-run-lifecycle ported once; existing main document/tail controls #823/#828/#831 retained. |
| `fix/api-dotnet-parity-owned-run` | `20a307d3c243` | superseded | Newest complete split runner fix/api-dotnet-parity-run-lifecycle ported once; existing main document/tail controls #823/#828/#831 retained. |
| `fix/api-dotnet-parity-review-fixes` | `a246ae193147` | superseded | Newest complete split runner fix/api-dotnet-parity-run-lifecycle ported once; existing main document/tail controls #823/#828/#831 retained. |
| `fix/api-dotnet-parity-run-evidence` | `f0fb3514552f` | dropped evidence | Functional split runner ported from fix/api-dotnet-parity-run-lifecycle; dated qualification reports and launch scripts omitted. |
| `fix/api-dotnet-parity-run-lifecycle` | `e266e26483ab` | ported | Complete split runner through e266e2648: counted lanes, cell gates, owned lifecycle, uncertain HTTP 408 cleanup, manual-clock controls. |
| `fix/api-dotnet-parity-runner` | `b19d97ed1495` | superseded | Newest complete split runner fix/api-dotnet-parity-run-lifecycle ported once; existing main document/tail controls #823/#828/#831 retained. |
| `fix/api-dotnet-raw-request-version` | `1cba4546628a` | ported | Explicit raw benchmark HTTP version 49e42aece; dated qualification files omitted. |
| `fix/api-etag-reuse` | `2ba9a3978784` | ported | Reuse already-computed point-GET validator and body/ETag regression (1daedb221, 103e3708a). No latency improvement claimed; dated reports omitted. |
| `fix/api-go-parity` | `62adf93c4caf` | already in main | Latest Go parity lifecycle, transport, attempt and tail contracts merged in #798/#799/#800/#801. |
| `fix/api-go-parity-live-d` | `1f6404958cb9` | dropped evidence | Dated rejected live-run/setup/recovery evidence; current bounded Go lifecycle #801 and retained evidence #812 already on main. No live run reactivated. |
| `fix/api-go-parity-live-d-diagnosis` | `b7d2f8818327` | dropped evidence | Dated rejected live-run/setup/recovery evidence; current bounded Go lifecycle #801 and retained evidence #812 already on main. No live run reactivated. |
| `fix/api-go-parity-live-recovery` | `ab71e8e16f1a` | ported | Reusable redacted synthetic plan failure classifier and parallel offline controls retained; dated deployment/setup/recovery evidence omitted. No live run reactivated. |
| `fix/api-keyed-keyless-diagnostic` | `d0c2733d034a` | dropped evidence | Experiment-only attribution lane and report; current durable surface/usage harness #775/#819/#864 retained. |
| `fix/api-measurement-boundaries` | `a7811d27056d` | dropped evidence | Historical observations/measurement audit only; no missing product implementation or reusable tool to port. Original commit retained in branch audit/bundle. |
| `fix/api-overhead-live` | `5e226c41e6d0` | superseded | Earlier hardcoded-region live benchmark/tenant lifecycle replaced by discovered-region SDK parity (#801 and ported .NET runner) and current tenant tooling. No obsolete credential workflow restored. |
| `fix/api-overhead-live-v2` | `0866b248d40d` | superseded | Earlier hardcoded-region live benchmark/tenant lifecycle replaced by discovered-region SDK parity (#801 and ported .NET runner) and current tenant tooling. No obsolete credential workflow restored. |
| `fix/api-public-pin-evidence` | `998e76f1e86e` | dropped evidence | Historical observations/measurement audit only; no missing product implementation or reusable tool to port. Original commit retained in branch audit/bundle. |
| `fix/api-regional-release-1059-evidence` | `7caa348edddc` | dropped evidence | Historical observations/measurement audit only; no missing product implementation or reusable tool to port. Original commit retained in branch audit/bundle. |
| `fix/api-runtime-budget-evidence` | `89a7e530f22f` | dropped evidence | Historical observations/measurement audit only; no missing product implementation or reusable tool to port. Original commit retained in branch audit/bundle. |
| `fix/api-runtime-budget-publication` | `a03bc48d0ebd` | dropped evidence | Historical observations/measurement audit only; no missing product implementation or reusable tool to port. Original commit retained in branch audit/bundle. |
| `fix/api-token-hash-allocation` | `b774760f3c60` | superseded | Candidate rejected by merged #816; retain current token hashing/cache behavior. |
| `fix/archive-native-writer-metering-rejection` | `3bb0f9c7cbcc` | dropped evidence | Rejected runtime remains rejected; #854 and ported real HTTP usage-writer controls cover the durable behavior. |
| `fix/b8-revert-engine-write-perf` | `2fca16a45e07` | superseded | Broad revert replaced by narrow idle-linger revert #654. Keep accepted collection/JSON/ETag improvements #644/#645/#647. |
| `fix/backup-slow-tigris-baseline` | `e7fa366ddb28` | already in main | Inventory merge adds no changes; existing main implementation takes precedence. |
| `fix/changes-shutdown-eof` | `a71e1e7ee8f5` | already in main | Current BoundedShutdownTests and bounded streaming shutdown cover cancellation/EOF; closed #527 old fixture not restored. |
| `fix/fast-suite-baseline-20261022` | `e7fa366ddb28` | already in main | Inventory merge adds no changes; existing main implementation takes precedence. |
| `fix/format-batch-391-397` | `c33afb99d8d4` | superseded | Coordinated format11 batch #399 and subsequent Turso engine #499 replace SQLite-era paths; no feature format bump or migration. |
| `fix/import-prepared-statements` | `aed12977136c` | already in main | Closed native statement reuse #366 superseded by prepared-command/session work #503/#506; current StatementReuseTests retained. |
| `fix/pg-wire-access-tokens` | `9d84340eaa0c` | already in main | Current Postgres shared token validation and live revocation supersede closed #523 implementation; no old handler restored. |
| `fix/put-usage-metering` | `81786cfc9ac3` | already in main | Per-region usage counts integrated #452 and evolved to per-node/current usage writer. |
| `fix/q09-affinity-is-stages` | `8dce2f6b0699` | ported | Bounded comparison affinity/IS, identifier handling and DELETE RETURNING; integrated with cached reads #852 and key optimization #781. |
| `fix/q09-hrana-database-identity` | `7e881ee8485a` | ported | Recovery batons bound to original immutable database identity (9d60b3a22, 7e881ee84). |
| `fix/r2-sender-bench-a` | `8c857077e756` | superseded | Current internal/repllocal harness #367 replaces flat command helpers; R2 envelope/cursor #376, R3 capture #370, R5 #364/#782, R7 #384 and R9 #399 already integrated. Frozen comparison baselines omitted. |
| `fix/r2-sender-bench-b` | `41230d25847a` | superseded | Current internal/repllocal harness #367 replaces flat command helpers; R2 envelope/cursor #376, R3 capture #370, R5 #364/#782, R7 #384 and R9 #399 already integrated. Frozen comparison baselines omitted. |
| `fix/r3-bench-after` | `042b5ba316b2` | superseded | Current internal/repllocal harness #367 replaces flat command helpers; R2 envelope/cursor #376, R3 capture #370, R5 #364/#782, R7 #384 and R9 #399 already integrated. Frozen comparison baselines omitted. |
| `fix/r3-bench-before` | `8197fcf56129` | superseded | Current internal/repllocal harness #367 replaces flat command helpers; R2 envelope/cursor #376, R3 capture #370, R5 #364/#782, R7 #384 and R9 #399 already integrated. Frozen comparison baselines omitted. |
| `fix/r5-anti-entropy-benchmark-harness` | `a8fd4538ba9a` | already in main | Current FeedCursorBench includes AntiEntropyDigestBench and AntiEntropyPullBench. |
| `fix/r5-anti-entropy-cost` | `e477c2f61789` | superseded | Anti-entropy bounds/catalog proof fixes integrated #364 and newer repair read work #782/#849 retained. |
| `fix/r5-r0-baseline-20261022` | `ee85648f102e` | superseded | Current internal/repllocal harness #367 replaces flat command helpers; R2 envelope/cursor #376, R3 capture #370, R5 #364/#782, R7 #384 and R9 #399 already integrated. Frozen comparison baselines omitted. |
| `fix/r5-r0-changed-20261022` | `676c0d2bdd8b` | superseded | Current internal/repllocal harness #367 replaces flat command helpers; R2 envelope/cursor #376, R3 capture #370, R5 #364/#782, R7 #384 and R9 #399 already integrated. Frozen comparison baselines omitted. |
| `fix/r6-isolate-intent-journal` | `5c5d7b653472` | superseded | Coordinated format11 batch #399 and subsequent Turso engine #499 replace SQLite-era paths; no feature format bump or migration. |
| `fix/r6-rebuild-allocations` | `2c93c66bb62a` | superseded | Source-frontier certification/GC #398 and coordinated #399 integrated; current Turso replica identity/rebuild ownership replaces SQLite-era code. |
| `fix/r6-rebuild-certification` | `8d3b7565a098` | superseded | Source-frontier certification/GC #398 and coordinated #399 integrated; current Turso replica identity/rebuild ownership replaces SQLite-era code. |
| `fix/r6-retire-idle-copy-intents` | `203425d6469f` | superseded | Coordinated format11 batch #399 and subsequent Turso engine #499 replace SQLite-era paths; no feature format bump or migration. |
| `fix/r9-benchmark-a-20261022` | `cbd986cc279a` | superseded | Current internal/repllocal harness #367 replaces flat command helpers; R2 envelope/cursor #376, R3 capture #370, R5 #364/#782, R7 #384 and R9 #399 already integrated. Frozen comparison baselines omitted. |
| `fix/r9-benchmark-b-20261022` | `8c9959595864` | superseded | Current internal/repllocal harness #367 replaces flat command helpers; R2 envelope/cursor #376, R3 capture #370, R5 #364/#782, R7 #384 and R9 #399 already integrated. Frozen comparison baselines omitted. |
| `fix/r9-merkle-reset` | `d9c524c4aa24` | superseded | Coordinated format11 batch #399 and subsequent Turso engine #499 replace SQLite-era paths; no feature format bump or migration. |
| `fix/r9-repair-forget-no-format` | `030ca766dcb0` | superseded | Projection cleanup/incarnation fixes folded into coordinated #399 and current Turso repair; do not restore old no-format fork. |
| `fix/replica-writer-bounds` | `4db6957f7d49` | ported | Newest 4db6957f7 writer batch bound (256 items/250 ms); retain #877 intent and current repair optimizations #782/#849. |
| `fix/replication-a6-proof-152d17be` | `152d17be3a77` | superseded | Renewed sender refill and concurrent fixture fixes integrated #346; later production repair coverage #678. Proof checkpoints omitted. |
| `fix/replication-idle-churn` | `6d9b441813e0` | already in main | Idle timers and repairs integrated by #782/#849; latest writer bound ported separately. |
| `fix/replication-main-baseline` | `e7fa366ddb28` | already in main | Inventory merge adds no changes; existing main implementation takes precedence. |
| `fix/replication-proof-36681092` | `36681092d149` | superseded | Renewed sender refill and concurrent fixture fixes integrated #346; later production repair coverage #678. Proof checkpoints omitted. |
| `fix/replication-proof-6b6cd95f` | `6b6cd95f8ee4` | superseded | Renewed sender refill and concurrent fixture fixes integrated #346; later production repair coverage #678. Proof checkpoints omitted. |
| `fix/replication-proof-b34e3300` | `b34e3300ca25` | superseded | Renewed sender refill and concurrent fixture fixes integrated #346; later production repair coverage #678. Proof checkpoints omitted. |
| `fix/replication-proof-b96ceaf4` | `b96ceaf420d4` | superseded | Renewed sender refill and concurrent fixture fixes integrated #346; later production repair coverage #678. Proof checkpoints omitted. |
| `fix/replication-proof-bf8d8a33` | `bf8d8a33e729` | superseded | Renewed sender refill and concurrent fixture fixes integrated #346; later production repair coverage #678. Proof checkpoints omitted. |
| `fix/replication-proof-ffd1aa34` | `ffd1aa34d3e0` | superseded | Renewed sender refill and concurrent fixture fixes integrated #346; later production repair coverage #678. Proof checkpoints omitted. |
| `fix/revert-bulk-io-cap` | `ab549515c13b` | superseded | Current storage admission and snapshot lanes evolved after #652; no closed canary broad-cap revert restored. |
| `fix/rewrite-drain-writer` | `f06f28f16e89` | superseded | Current healthy rewrite #707 replaces closed #706/earlier drain implementation; retain fault-aware replay and current ownership. |
| `fix/ship-dirty-ci-proof` | `110e61b6ffff` | dropped evidence | Historical ship qualification checkpoints; current bootstrap/readiness and internal/repllocal paths supersede old proof fixtures. |
| `fix/ship-dirty-measurement` | `988ebdee5320` | already in main | Inventory merge adds no changes; existing main implementation takes precedence. |
| `fix/ship-dirty-stage-proof` | `0a08551e26bf` | dropped evidence | Historical ship qualification checkpoints; current bootstrap/readiness and internal/repllocal paths supersede old proof fixtures. |
| `fix/ship-dirty-telemetry-proof` | `988ebdee5320` | already in main | Inventory merge adds no changes; existing main implementation takes precedence. |
| `fix/sink-bounded-transactions` | `332fbe37f32d` | superseded | Newer fix/replica-writer-bounds 4db6957f7 ported once, including atomic importer preservation. |
| `fix/slow-catalog-evidence` | `e7fa366ddb28` | already in main | Inventory merge adds no changes; existing main implementation takes precedence. |
| `fix/slow-catalog-gate-baseline-fce45959` | `fce45959070d` | already in main | Inventory merge adds no changes; existing main implementation takes precedence. |
| `fix/slow-http-baseline-e7fa366d` | `e7fa366ddb28` | already in main | Inventory merge adds no changes; existing main implementation takes precedence. |
| `fix/slow-keys-evidence` | `e7fa366ddb28` | already in main | Inventory merge adds no changes; existing main implementation takes precedence. |
| `fix/slow-query-evidence` | `a4113a2fe1b9` | superseded | Current bounded-count fixture and retired shape design already evolved after #342/#531/#564; historical Slow proof not ported. |
| `fix/terrabase-auth-temporary-allocations` | `4cba57edbe8b` | dropped evidence | Combined auth candidate was preregistered, not launched; requires separate approval/source review and does not reopen #814/#816. Shared CPU observer retained once. |
| `fix/terrabase-cached-system-open` | `8aaef93e8e65` | superseded | Cached-open runtime remains held for CPU/p50/p99 concerns recorded by merged #836; no activation. Shared external CPU observer retained once. |
| `fix/terrabase-cached-system-open-ab` | `7b3980c656ed` | superseded | Cached-open runtime remains held for CPU/p50/p99 concerns recorded by merged #836; no activation. Shared external CPU observer retained once. |
| `fix/terrabase-cached-system-open-common-r2-evidence` | `289794d2873b` | superseded | Cached-open runtime remains held for CPU/p50/p99 concerns recorded by merged #836; no activation. Shared external CPU observer retained once. |
| `fix/terrabase-database-create-stage-metrics` | `673ed085f32c` | superseded | Latest complete version from fix/api-create-stage-qualification ported once; closed #833/#842 were not design rejections. |
| `fix/terrabase-qos-fixture-clock` | `5f5c00014980` | already in main | Inventory merge adds no changes; existing main implementation takes precedence. |
| `fix/ts1132-http-region-guidance` | `e706ca8fb109` | already in main | Current HTTP listener/pin error contract #810 takes precedence; only missing socket guidance ported. |
| `fix/ts994-backup-registered-only` | `172864bb9235` | already in main | Registered-file background admission integrated #653 and recovery safeguards #704; current fail-closed recovery retained. |
| `fix/usage-native-response-writer` | `47053ce23b89` | superseded | Native writer candidate rejected for preflush error corruption; current runtime #854 wins. Durable regressions retained from usage-writer controls. |
| `fix/usage-native-writer-metering` | `328256573985` | superseded | Native writer candidate rejected for preflush error corruption; current runtime #854 wins. Durable regressions retained from usage-writer controls. |
| `fix/usage-writer-error-controls` | `5e089494c1b1` | ported | Retain real HTTP body-byte, preflush exception, abort and minute-boundary regressions; current runtime #854 preserved. Duplicate UsageWriterTests retained once. |
| `fix/usage-writer-metering-controls` | `73bec762f795` | ported | Retain real HTTP body-byte, preflush exception, abort and minute-boundary regressions; current runtime #854 preserved. Duplicate UsageWriterTests retained once. |
| `gh-readonly-queue/main/pr-740-74073afa8eb9cc44efd9fd71e2462b4ba9187963` | `7ac834060c15` | already in main | Inventory merge adds no changes; existing main implementation takes precedence. |
| `hotfix/legacy-key-secret-bindings` | `de2f143ac913` | already in main | Inventory merge adds no changes; existing main implementation takes precedence. |
| `perf/api-speedup` | `30022135f633` | already in main | Inventory merge adds no changes; existing main implementation takes precedence. |
| `pr-assets` | `28a2b686a193` | superseded | Images remain at immutable 28a2b686a commit; replace #882 raw branch URLs with immutable SHA before deleting ref. No PNGs added to product tree. |
| `refactor/local-e2e-backups` | `cf3640282ff4` | superseded | OCI runtime/local qualification #359 and current ts-orch ownership replace these older stacks. Destination-prefix resume and isolated local E2E remain in current code; retired compute files not restored. |
| `refactor/local-e2e-scenarios` | `5838274bef5b` | superseded | OCI runtime/local qualification #359 and current ts-orch ownership replace these older stacks. Destination-prefix resume and isolated local E2E remain in current code; retired compute files not restored. |
| `refactor/oci-auth-docs-cleanup` | `b3efffae2afc` | superseded | OCI runtime/local qualification #359 and current ts-orch ownership replace these older stacks. Destination-prefix resume and isolated local E2E remain in current code; retired compute files not restored. |
| `refactor/oci-benchmark-cleanup` | `f79f1fe22656` | superseded | OCI runtime/local qualification #359 and current ts-orch ownership replace these older stacks. Destination-prefix resume and isolated local E2E remain in current code; retired compute files not restored. |
| `refactor/oci-cursor-resume` | `d7709113f972` | superseded | OCI runtime/local qualification #359 and current ts-orch ownership replace these older stacks. Destination-prefix resume and isolated local E2E remain in current code; retired compute files not restored. |
| `refactor/oci-docs-cleanup` | `6206bf46666e` | already in main | Inventory merge adds no changes; existing main implementation takes precedence. |
| `refactor/oci-replication-docs` | `4d95901e5610` | superseded | OCI runtime/local qualification #359 and current ts-orch ownership replace these older stacks. Destination-prefix resume and isolated local E2E remain in current code; retired compute files not restored. |
| `refactor/oci-runtime` | `9b590429620e` | superseded | OCI runtime/local qualification #359 and current ts-orch ownership replace these older stacks. Destination-prefix resume and isolated local E2E remain in current code; retired compute files not restored. |
| `refactor/oci-singleton-identity` | `67ef4ff4f7c3` | superseded | OCI runtime/local qualification #359 and current ts-orch ownership replace these older stacks. Destination-prefix resume and isolated local E2E remain in current code; retired compute files not restored. |
| `refactor/oci-terrabase-runtime-docs` | `d5939631f936` | superseded | OCI runtime/local qualification #359 and current ts-orch ownership replace these older stacks. Destination-prefix resume and isolated local E2E remain in current code; retired compute files not restored. |
| `refactor/oci-test-process-isolation` | `91ff41600a8e` | superseded | OCI runtime/local qualification #359 and current ts-orch ownership replace these older stacks. Destination-prefix resume and isolated local E2E remain in current code; retired compute files not restored. |
| `refactor/rewrite-replay` | `b35537da277c` | superseded | Current healthy rewrite #707 replaces closed #706/earlier drain implementation; retain fault-aware replay and current ownership. |
| `refactor/scratch-coldopen` | `fdc33a5f49b4` | dropped evidence | Explicit diagnostic/not-for-merge scratch; no product change. |
| `refactor/scratch-main-baseline` | `6475a24d7e87` | already in main | Inventory merge adds no changes; existing main implementation takes precedence. |
| `refactor/scratch-plan-pro` | `8428caf80b4e` | dropped evidence | Explicit diagnostic/not-for-merge scratch; no product change. |
| `refactor/scratch-prepare-count` | `90c12219a4e6` | dropped evidence | Explicit diagnostic/not-for-merge scratch; no product change. |
| `refactor/scratch-sql-bench-after` | `7b36811b852b` | superseded | SQL key optimization #781, cached reads #852 and current performance harness #819/#850/#873 supersede scratch baselines. |
| `refactor/scratch-sql-bench-before` | `606445d751ac` | superseded | SQL key optimization #781, cached reads #852 and current performance harness #819/#850/#873 supersede scratch baselines. |
| `refactor/scratch-sql-bench-cache` | `5c58998c8ede` | superseded | SQL key optimization #781, cached reads #852 and current performance harness #819/#850/#873 supersede scratch baselines. |
| `rt2/crash-restart` | `7da2ee1aebcb` | superseded | Crash restart and read-only intent behavior integrated #511/#537 and later write lifecycle. Do not restore retired SQLite engine. |
| `rt2/free-reads-1` | `f125d33dbdd9` | already in main | Shape single-flight/mirror/edge/cache work integrated #516/#521/#538/#545; later frontier sealing #497 retained. |
| `rt2/free-reads-2` | `95bc86cfde39` | already in main | Shape single-flight/mirror/edge/cache work integrated #516/#521/#538/#545; later frontier sealing #497 retained. |
| `rt2/free-reads-3` | `2899869608fd` | already in main | Shape single-flight/mirror/edge/cache work integrated #516/#521/#538/#545; later frontier sealing #497 retained. |
| `rt2/free-reads-4` | `f51ed6c4e0ab` | already in main | Shape single-flight/mirror/edge/cache work integrated #516/#521/#538/#545; later frontier sealing #497 retained. |
| `rt2/free-reads-5` | `2558412fb5e0` | already in main | Shape single-flight/mirror/edge/cache work integrated #516/#521/#538/#545; later frontier sealing #497 retained. |
| `rt2/free-repl` | `09ee5c2a53ca` | superseded | Current live repl-bytes tools and report #565 replace early duplicated harness; dated billing/RTT evidence omitted. |
| `rt2/free-repl-2` | `09ee5c2a53ca` | superseded | Current live repl-bytes tools and report #565 replace early duplicated harness; dated billing/RTT evidence omitted. |
| `rt2/intents` | `8fe4d5d490f9` | superseded | Crash restart and read-only intent behavior integrated #511/#537 and later write lifecycle. Do not restore retired SQLite engine. |
| `rt2/lag-3` | `cab938477a41` | already in main | Lag instruments/protocol/pipelining integrated #504/#536/#557 and published #618; later receiver fixes retained. |
| `rt2/lag-4` | `5f429510c55e` | already in main | Lag instruments/protocol/pipelining integrated #504/#536/#557 and published #618; later receiver fixes retained. |
| `rt2/lag-results` | `216a8efa23fb` | already in main | Lag instruments/protocol/pipelining integrated #504/#536/#557 and published #618; later receiver fixes retained. |
| `rt2/shape-qos` | `68cd06b9c6a0` | already in main | Current catalog token cache and read-lane/shared-answer QoS supersede old stack; shape implementation #516/#545 retained. |
| `rt2/shape-qos-1` | `772775f5725d` | already in main | Current catalog token cache and read-lane/shared-answer QoS supersede old stack; shape implementation #516/#545 retained. |
| `rt2/shape-qos-2` | `68cd06b9c6a0` | already in main | Current catalog token cache and read-lane/shared-answer QoS supersede old stack; shape implementation #516/#545 retained. |
| `rt2/stmt-catalog-1` | `61367a039120` | already in main | Prepared commands/storage sessions and token catalog cache integrated #503/#506; current auth/cache behavior retained. |
| `rt2/stmt-catalog-2` | `3504f0e8fd4d` | already in main | Prepared commands/storage sessions and token catalog cache integrated #503/#506; current auth/cache behavior retained. |
| `rt2/stmt-catalog-3` | `7b36acee1daf` | already in main | Prepared commands/storage sessions and token catalog cache integrated #503/#506; current auth/cache behavior retained. |
| `rt2c/free-repl-results` | `a088f87d0e1f` | superseded | Current replication-byte/live tools #565 supersede early report stack; no dated evidence reintroduced. |
| `rt2c/lag-results` | `2699d5307dda` | superseded | Protocol instrumentation/pipelining integrated #504/#536/#557 and report #618; frozen A/B checkpoints omitted. |
| `rt2c/perf-bench` | `c99d5b60202a` | superseded | Prepared/session/cache optimizations and VsTurso harness integrated #503/#506/#561/#583/#585. Idle-linger #638 intentionally reverted #654; accepted #644/#645/#647 remain. Frozen benchmark stack omitted. |
| `rt2c/perf-bench-before` | `2db90053ec4e` | superseded | Prepared/session/cache optimizations and VsTurso harness integrated #503/#506/#561/#583/#585. Idle-linger #638 intentionally reverted #654; accepted #644/#645/#647 remain. Frozen benchmark stack omitted. |
| `rt2c/perf-results` | `8dfa0f881b49` | superseded | Prepared/session/cache optimizations and VsTurso harness integrated #503/#506/#561/#583/#585. Idle-linger #638 intentionally reverted #654; accepted #644/#645/#647 remain. Frozen benchmark stack omitted. |
| `rt2c/perf-results-local` | `b0ad03195393` | superseded | Prepared/session/cache optimizations and VsTurso harness integrated #503/#506/#561/#583/#585. Idle-linger #638 intentionally reverted #654; accepted #644/#645/#647 remain. Frozen benchmark stack omitted. |
| `rt2c/perf2-bench` | `7bde93965a8e` | superseded | Prepared/session/cache optimizations and VsTurso harness integrated #503/#506/#561/#583/#585. Idle-linger #638 intentionally reverted #654; accepted #644/#645/#647 remain. Frozen benchmark stack omitted. |
| `rt2c/perf2-bench-before` | `b31bc0caa611` | superseded | Prepared/session/cache optimizations and VsTurso harness integrated #503/#506/#561/#583/#585. Idle-linger #638 intentionally reverted #654; accepted #644/#645/#647 remain. Frozen benchmark stack omitted. |
| `rt2c/perf2-p3only` | `aa63ce023fd6` | superseded | Prepared/session/cache optimizations and VsTurso harness integrated #503/#506/#561/#583/#585. Idle-linger #638 intentionally reverted #654; accepted #644/#645/#647 remain. Frozen benchmark stack omitted. |
| `rt2c/perf2-results` | `228548c8ff14` | superseded | Prepared/session/cache optimizations and VsTurso harness integrated #503/#506/#561/#583/#585. Idle-linger #638 intentionally reverted #654; accepted #644/#645/#647 remain. Frozen benchmark stack omitted. |
| `rt2c/perf3-2` | `f778370f433f` | superseded | Prepared/session/cache optimizations and VsTurso harness integrated #503/#506/#561/#583/#585. Idle-linger #638 intentionally reverted #654; accepted #644/#645/#647 remain. Frozen benchmark stack omitted. |
| `rt2c/perf3-4` | `418a0237152e` | superseded | Prepared/session/cache optimizations and VsTurso harness integrated #503/#506/#561/#583/#585. Idle-linger #638 intentionally reverted #654; accepted #644/#645/#647 remain. Frozen benchmark stack omitted. |
| `rt2c/perf3-5` | `20e55b9102cc` | superseded | Prepared/session/cache optimizations and VsTurso harness integrated #503/#506/#561/#583/#585. Idle-linger #638 intentionally reverted #654; accepted #644/#645/#647 remain. Frozen benchmark stack omitted. |
| `rt2c/perf3-6` | `9b565c17d176` | superseded | Prepared/session/cache optimizations and VsTurso harness integrated #503/#506/#561/#583/#585. Idle-linger #638 intentionally reverted #654; accepted #644/#645/#647 remain. Frozen benchmark stack omitted. |
| `rt2c/perf3-bench` | `0a2950d55a7f` | superseded | Prepared/session/cache optimizations and VsTurso harness integrated #503/#506/#561/#583/#585. Idle-linger #638 intentionally reverted #654; accepted #644/#645/#647 remain. Frozen benchmark stack omitted. |
| `rt2c/perf3-bench-before` | `2b7f9f928a54` | superseded | Prepared/session/cache optimizations and VsTurso harness integrated #503/#506/#561/#583/#585. Idle-linger #638 intentionally reverted #654; accepted #644/#645/#647 remain. Frozen benchmark stack omitted. |
| `rt2c/perf3-results-wip` | `8dfa0f881b49` | superseded | Prepared/session/cache optimizations and VsTurso harness integrated #503/#506/#561/#583/#585. Idle-linger #638 intentionally reverted #654; accepted #644/#645/#647 remain. Frozen benchmark stack omitted. |
| `rt2c/reads-1k` | `8d541e8f41d3` | superseded | Shape cache/mirror/edge/read QoS and frontier work integrated #497/#516/#521/#538/#545; diagnostic not-for-merge token cache and dated reports omitted. |
| `rt2c/reads-2` | `39b5e4159f77` | superseded | Shape cache/mirror/edge/read QoS and frontier work integrated #497/#516/#521/#538/#545; diagnostic not-for-merge token cache and dated reports omitted. |
| `rt2c/reads-3` | `1d371838e15f` | superseded | Shape cache/mirror/edge/read QoS and frontier work integrated #497/#516/#521/#538/#545; diagnostic not-for-merge token cache and dated reports omitted. |
| `rt2c/reads-5` | `88a363f8b3f5` | superseded | Shape cache/mirror/edge/read QoS and frontier work integrated #497/#516/#521/#538/#545; diagnostic not-for-merge token cache and dated reports omitted. |
| `rt2c/reads-6` | `44796f5a6581` | superseded | Shape cache/mirror/edge/read QoS and frontier work integrated #497/#516/#521/#538/#545; diagnostic not-for-merge token cache and dated reports omitted. |
| `rt2c/reads-7` | `c9283881ccb4` | superseded | Shape cache/mirror/edge/read QoS and frontier work integrated #497/#516/#521/#538/#545; diagnostic not-for-merge token cache and dated reports omitted. |
| `rt2c/reads-bench` | `ff47738046a1` | superseded | Shape cache/mirror/edge/read QoS and frontier work integrated #497/#516/#521/#538/#545; diagnostic not-for-merge token cache and dated reports omitted. |
| `rt2c/reads-bench-local` | `f51a583bcf62` | superseded | Shape cache/mirror/edge/read QoS and frontier work integrated #497/#516/#521/#538/#545; diagnostic not-for-merge token cache and dated reports omitted. |
| `rt2c/reads-fix-2` | `050096c3d386` | superseded | Shape cache/mirror/edge/read QoS and frontier work integrated #497/#516/#521/#538/#545; diagnostic not-for-merge token cache and dated reports omitted. |
| `rt2c/reads-results` | `7fc8814e558b` | superseded | Shape cache/mirror/edge/read QoS and frontier work integrated #497/#516/#521/#538/#545; diagnostic not-for-merge token cache and dated reports omitted. |
| `rt2c/tests-matrix` | `c4b34be182d1` | superseded | Catalog restart and SQL replication matrix integrated #455 and current E2E; dated matrix results omitted. |
| `rt2c/tests-matrix-f1` | `366a74b90281` | superseded | Catalog restart and SQL replication matrix integrated #455 and current E2E; dated matrix results omitted. |
| `rt2c/tests-matrix-results` | `fe2c2d05af67` | superseded | Catalog restart and SQL replication matrix integrated #455 and current E2E; dated matrix results omitted. |
| `rt2c/wire-1` | `688b85df4c0e` | superseded | Wire listener, Fast protocol coverage and VsTurso lanes integrated #528/#540/#556/#585; frozen experiment stack omitted. |
| `rt2c/wire-2` | `ae8759ea8230` | superseded | Wire listener, Fast protocol coverage and VsTurso lanes integrated #528/#540/#556/#585; frozen experiment stack omitted. |
| `rt2c/wire-3` | `e31eee7a4f54` | superseded | Wire listener, Fast protocol coverage and VsTurso lanes integrated #528/#540/#556/#585; frozen experiment stack omitted. |
| `t3/dotnet-parity-runner` | `8c0adb8d3026` | superseded | Newest complete split runner fix/api-dotnet-parity-run-lifecycle ported once; existing main document/tail controls #823/#828/#831 retained. |
