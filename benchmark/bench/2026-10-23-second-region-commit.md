# Second-region durable commit: local proof and partition tradeoffs

TS-771: all three fresh-process repeats passed local durability/partition assertions and correlated every one of their 1,000 load documents. **Actual origin durable-COMMIT→remote durable-apply p99 was 1,660.83 / 2,902.69 / 410.99 ms.** The <200 ms target remains unproven on this shared host. These are paired postcommit measurements, independent of origin execution and ACK return; they establish neither the cause of the tails nor an OCI/WAN performance guarantee.

## Reproduce and identify

Measured harness commit: `e9ce5587fc9b561f2cd1408d3d019129eda94c22`; unchanged TerraBase source tree: `5e4c6be4945bfb98fecad878e7d0bfaed6fade99`; base `49e32e99c`. #398 is included; #423 is not needed or included, and its benchmark hold remains. Measurement ran 2026-10-23 11:58:32–12:01:39 UTC. Publication first rebased onto main `e9e2ac47a5560812dc4da098496f02a4528f69ad`, whose TerraBase source tree is `d2a24462cbeeaf0f44c796cb396eb26235e6e007`. Since measurement, main adds PostgreSQL syntax/type/catalog support, an authenticated operator endpoint for retrying existing certified tenant rebuilds, auth release-identity checks and CI/test corrections. The operator endpoint changes replication recovery administration; these results do not measure that endpoint or certify the newer source tree. Measurements and binary hashes retain the exact older source identity above; main drift was not remeasured. Later publication rebases also include main's import destination-region correction (#471). These publication rebases do not extend the measured-source claim. Final-head CI remains required. [Raw paired evidence](2026-10-23-second-region-lag-raw.json), [generated summary](2026-10-23-second-region-lag-raw.md) and [verification/provenance](2026-10-23-second-region-lag-validation.json) retain all three repeats and binary hashes.

Host: `mariogk-dev`, Release .NET 11.0.100-rc.1.26425.128, Go 1.27.3, 46 visible CPUs. Shared host, no exclusive CPU reservation; sampled 1-minute load 95.08–140.87, with other jobs active and CPU pressure recorded in raw evidence. Load/pressure alone cannot attribute a slow operation to scheduling, storage or replication. Pinning here names the host, not CPU affinity. The requested existing checkout is already on mariogk-dev; `ts-heavy-any` would prepare another checkout, so the explicitly permitted local workflow was used.

```sh
nice -n 19 env TsDotnetRoot=/home/mariogk/Projects/TerraScale/ts-dotnet make terrabase-build CONFIGURATION=Release
(cd benchmark && nice -n 19 go build -trimpath -o bin/repl-local ./cmd/repl-local)
nice -n 19 env TS_HEAVY_HOST=mariogk-dev benchmark/bin/repl-local --scenario second-region --docs 1000 --repeats 3 --out benchmark/bench/2026-10-23-second-region-lag-raw
```

Two independent authenticated test-server OS processes, gru/sjc, each with its own real SQLite files. gRPC peer traffic crosses byte-counting loopback TCP proxies. Test-only membership stands in for ts-orch discovery; test-issued credentials/control files stay in private temporary directories. The reused bootstrap also places fra, but there is no fra process or alternate confirming region. No production knob, feature flag, storage-format, workflow or replication implementation change.

Each fresh cluster receives 1,000 unique individual PUTs from gru at concurrency 16, deterministic seed 20261022, body mix 256 B 70%, 1 KiB 25%, 8 KiB 5%. Exact ID/content hash verification found 1,000/1,000 on both replicas in each repeat: zero missing, extra, mismatched, unknown, failed or retried load writes. Separate low-rate visibility probes write from both regions.

## Contract and measurement boundaries

The acceptance target is leaderless LWW with **commit-to-remote-durable-apply p99 <200 ms**. [0013a §7](../../docs/terrabase/decisions/0013a-remote-durable-acknowledgement.md) explicitly says the 10-second remote-confirmation ceiling is not a latency promise. Its measurement reference, [0013b §11](../../docs/terrabase/decisions/0013b-operation-idempotency.md), separates local B1, shipping, receiver B2, ACK return and client response. These unkeyed PUTs do not perform the proposed keyed reservation round. Client barrier latency includes origin work and the reverse ACK; the lag target does not.

The existing test-host lifecycle hook `writer.marker` records CLOCK_MONOTONIC nanoseconds on the SQLite worker immediately after native origin COMMIT returns, before its final CDC marker SELECT and before releasing the exclusive writer. The exact marker and source replica identity pair that timestamp with the receiver's existing `AfterSqliteCommit` hook. A receiver transaction can contain multiple whole source groups; its native completion applies to every document in those groups. The hook is observed after COMMIT returns, not at the disk device: finite callback/scheduling overhead is not calibrated or subtracted. Receiver entry, native completion and sink return are retained separately. Same Linux boot, time namespace, nanosecond frequency and stable process identity are required. No wall-clock/HLC arithmetic is used.

Every load document must occur exactly once in the matched collection; missing/duplicate/noncausal markers or documents, clock mismatch, dropped samples and load retries fail the proof. Three thousand documents matched across 202 / 203 / 200 source commit groups. Percentiles use nearest rank per repeat. Document weighting counts each document; group weighting counts each source commit once. No percentile subtraction or histogram pooling is used.

The source marker-identification SELECT adds measurement work while holding the writer. Its p99 was 0.368 / 3.078 / 0.548 ms (maximum 0.778 / 21.758 / 0.754 ms); it can overlap receiver work and is never subtracted. Bounded in-memory timing capture and callback work can perturb scheduling; there is no per-commit timing-file write. The receiver endpoint excludes cleanup, stable-frontier lookup, reverse ACK and client response.

## Measured healthy commits

All times below are milliseconds. The workload, concurrency and active visibility probes were retained.

| Repeat | docs/s | Commit→apply document p50 / p99 | Commit→apply group p99 (groups) | Before receiver p99 | Receiver entry→native COMMIT p99 | Postcommit cleanup p99 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 27.19 | 147.06 / 1660.83 | 1507.83 (202) | 856.19 | 1121.56 | 12.25 |
| 2 | 16.94 | 201.35 / 2902.69 | 2902.69 (203) | 1011.14 | 2775.40 | 22.61 |
| 3 | 42.20 | 154.26 / 410.99 | 410.99 (200) | 257.03 | 332.53 | 17.63 |

Component p99 values belong to different samples and must not be added. The following are the **same document's** actual stages at rank 990/1,000, with exact markers retained for diagnosis:

| Repeat | Source marker | Documents sharing source group | Origin COMMIT→receiver entry | Receiver entry→native COMMIT | Sum = actual lag |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1 | 287 | 14 | 837.482432 | 823.350954 | 1660.833386 |
| 2 | 1730 | 8 | 36.600715 | 2866.092576 | 2902.693291 |
| 3 | 185 | 12 | 158.175817 | 252.810431 | 410.986248 |

The repeat-2 tail is inside receiver entry→durable native COMMIT; repeat 1 spans both stages. This precisely locates observed delay but does not separate writer admission, scheduler delay, joining/index work, SQLite I/O and sync. R10 owns that diagnosis: replay these marker/stage pairs with per-operation source shipping/read/wake and receiver writer-queue/apply/sync scheduling evidence under the same workload, then repeat the target measurement. Existing snapshots contain queue gauges but lack those per-operation durations. This task does not alter R10's runtime or weaken workload to obtain a passing number. An uncontended baseline needs coordination with the dev-host owner; the authorized shared-host evidence cannot prove a replication-only root cause or the <200 ms target. Parent's final one-time host check found no quiet qualifying host: delta load1 49.22 / 16 CPUs (score 3.076), mariogk-dev 67.14 / 46 CPUs (score 1.460), beta unreachable. No extra repeats were run to seek a passing number; parent must coordinate an uncontended baseline or R10's operation-level diagnosis.

| Repeat | Dispatch→local commit p50 / p99 | Full HTTP ACK p50 / p99 | gru→sjc dispatch→visible p99 | sjc→gru dispatch→visible p99 |
| --- | ---: | ---: | ---: | ---: |
| 1 | 159.40 / 1143.50 | 370.18 / 2539.52 | 2422.78 | 1286.14 |
| 2 | 249.30 / 4446.40 | 593.92 / 4845.57 | 4102.14 | 3747.84 |
| 3 | 171.30 / 428.40 | 371.46 / 636.93 | 610.30 | 452.86 |

Dispatch→local commit includes origin admission/queueing; its histogram includes gru visibility writes. Full HTTP ACK includes transport, origin work, replication, durable receiver commit and response. Dispatch→visible includes origin execution, 10 ms polling and GET latency; it is not used to judge replication lag. Explicit single-send probes in both regions require `201`, `durability:"remote_durable"` and exact values on both replicas.

The shared `probeRequest` now retries PUT only on typed pre-execution `remote_region_unavailable` or `confirmation_busy`. `write_outcome_unknown`, malformed/untyped 503 and `storage_busy` stop the probe immediately, even with Retry-After; transient GET retries remain. The unknown-then-success regression asserts exactly one PUT and no successful retry. The [original evidence](2026-10-23-second-region-raw.json) is historical: its dispatch-to-visible values neither measure commit-to-apply lag nor have this retry protection, and are superseded for those claims.

## Measured partition tradeoffs

After healthy probes, the test-host-only gate holds the matching receiver ACK **after real apply commits**. Both TCP proxies are disconnected while the PUT is still pending. Each fault probe is sent exactly once; generic 5xx retry logic is bypassed.

| Repeat | Partition→unknown response ms | New-write refusal gru / sjc ms | Heal→new remote-durable success ms |
| --- | ---: | ---: | ---: |
| 1 | 89.21 | 4.51 / 5.15 | 1111.04 |
| 2 | 132.34 | 9.39 / 82.97 | 976.80 |
| 3 | 52.53 | 18.88 / 12.15 | 989.77 |

Every repeat asserted: the already-committed PUT returned `503 write_outcome_unknown`, no Retry-After and no durability claim, with its exact value readable on both isolated replicas. Each isolated region then returned `503 remote_region_unavailable` with Retry-After for a fresh PUT; that ID was absent on both replicas. Reconnecting restored an explicitly remote-durable new write. Only typed pre-execution refusals are retried during healing; ambiguous outcomes are never replayed by this proof.

The tradeoff is visible: a partition prevents new successful writes; a committed write can return an ambiguous client outcome even when both copies exist. This provides neither global serializability nor a backup-commit guarantee. All workers exited successfully; the existing harness classified one proxy reset per repeat as expected teardown traffic.

## Implementation and executed evidence

| Barrier | Existing implementation and executed integration evidence |
| --- | --- |
| Origin durability | [SQLite writer](../../src/TerraBase/Engine/Sqlite/SqliteDatabaseStore.cs) commits with FULL. [RemoteAckE2ETests](../../tests/TerraBase.E2E/RemoteAckE2ETests.cs) passed all 3 cases: immediate origin SIGKILL, remote restart on its pinned disk, and recovery of the origin CDC log to a region that missed confirmed writes. |
| Remote durable apply | [Receiver](../../src/TerraBase/Features/Replication/PeerStreamReceiver.cs) ACK follows real sink application. [ApplyDurabilityTests](../../tests/TerraBase.Fast/Replication/ApplyDurabilityTests.cs) passed: both receiver apply transactions reported FULL (`2, 2`) before ACK. |
| Truthful client response | [DocumentService](../../src/TerraBase/Features/Documents/DocumentService.cs) releases its writer then waits for its own marker through [PeerStreams](../../src/TerraBase/Features/Replication/PeerStreams.cs). All 39 [RemoteAckTests](../../tests/TerraBase.Fast/Replication/RemoteAckTests.cs) passed, including held/lost/refused ACK, cancellation, missing marker, stale incarnation and pre-execution refusal. |
| Partition | All 3 new process-harness repeats passed the assertions above; no production networking or resources changed. |
| Leaderless/LWW | Both regions accepted remote-durable writes; LWW implementation is unchanged. The existing [three-region overwrite test](../../tests/TerraBase.E2E/ThreeRegionE2ETests.cs), `Burst_of_overwrites_from_three_regions_converges_to_a_last_write`, also passed locally (120 concurrent overwrites, final winner is one region's last write). A second-region copy does not create a global transaction order. |

Executed commands used `nice -n 19 dotnet run --configuration Release --no-build` with project Fast and separate filters `/*/*/RemoteAckTests/*` (39) and `/*/*/ApplyDurabilityTests/*` (1), and project E2E filter `/*/*/RemoteAckE2ETests/*` (3). The additional E2E LWW filter `/*/*/ThreeRegionE2ETests/Burst_of_overwrites_from_three_regions_converges_to_a_last_write` also passed (1); `env -u TERRABASE_E2E_ORCH_NODES` forced its local in-process cluster. All 44 selected cases passed with zero skips. These 44 cases were executed at `f9590ee3`, before the timing extension; they were not rerun for it, and their production source tree is unchanged. The timing extension's Release worker build passed with zero warnings/errors; focused Go packages `cmd/repl-local`, `internal/repllocal`, `internal/replbench` passed. [Test results and binary SHA-256](2026-10-23-second-region-tests.json) retain individual test identities. The new timing validation tests reject invalid pairs and verify document/group weighting; partition setup also waits for collection visibility in every region before its first single-send PUT. These are affected checks; full current-head CI remains the merge gate. Old-head CI at `13dff410` failed two unchanged ShapeMirror SigV4 tests. The test fixture froze time before server startup; crossing the signing epoch left the manifest expiry behind the AWS signer's wall clock. The default fixture now follows wall time plus manual advances, the epoch-sensitive retention case explicitly keeps a frozen FakeTimeProvider, and the valid-SigV4 test asserts its clock follows wall time after startup. A separate worker's isolated Release build had zero warnings/errors; five default-parallel runs passed 17/17 each: **85 passed, 0 failed, 0 skipped**. [Sanitized fixture validation](2026-10-23-shape-mirror-validation.json) retains log hashes and per-run counts. These runs precede the rebase; current-head CI is still required.

## Production boundary and remaining gates

Read-only GET `https://gru.api.terrascale.tech/healthz` on 2026-10-23 returned HTTP 200: status ok, version 0.1.0, region gru, revision `888ea413c06f6452cccceab57c735fa35b4d850d`, storageFormat `11-022325879f76`, machine `gru-1-ctl-1`. This identifies one live artifact, not the measured harness/source revision or a remote serving copy.

The clean [ts-orch deployment source](https://github.com/TerraScale/ts-orchestrator/blob/1ed9883126b0c4c4635b0ca9df9fdef8fd4bc4ee/deploy/m1/services/terrabase.json) declares only `["gru"]`, image digest `509165626e0ef74e10a711e4bd1be61b35e4ea238f1e3b8780ff1b7afe509a7e` (manifest SHA-256 `fc87d60487fc935d405090d40ed4993c547699d67e15d8e037f7e303081301bb`). No deployed second-region barrier is claimed. No deployment change was made.

This local proof does not verify OCI mesh/WAN behavior, region failure independence, physical power loss/fsync hardware guarantees, backup persistence or the running artifact's full contract. Preserve current-head CI/review, performance and deployment/release blockers. Parent owns review, merge and ticket completion. Development attempts with invalid marker/collection IDs, a wrapped-store cast, and a sink-return diagnostic followed by a setup 404 are excluded. Only the complete three-repeat post-native-COMMIT run above is lag evidence.
