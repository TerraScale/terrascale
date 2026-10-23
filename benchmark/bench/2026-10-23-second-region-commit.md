# Second-region durable commit: local proof and partition tradeoffs

TS-771: the local durability/partition assertions passed in all three independent repeats. **The p99 <200 ms performance gate remains open:** every measured dispatch-to-visible p99 exceeded 200 ms. No WAN or production second-region guarantee is established.

## Reproduce and identify

Measured commit: `f9590ee3102a0ae65a0eee25d5081b37b0604176` (harness); unchanged TerraBase source tree: `5e4c6be4945bfb98fecad878e7d0bfaed6fade99`; base `49e32e99c`. #398 is included; #423 is not needed or included, and its benchmark hold remains. Source/test/harness paths were clean at measurement; the report commit only adds evidence.

Host: `mariogk-dev`, Release .NET 11.0.100-rc.1.26425.128, Go 1.27.3, 46 visible CPUs. Shared host, no exclusive CPU reservation; sampled 1-minute load 23.16–60.29. Other jobs were active. No causal attribution or cross-host performance comparison is made. Pinning here names the host, not CPU affinity. The requested existing checkout is already on mariogk-dev; `ts-heavy-any` would prepare another checkout, so the explicitly permitted local workflow was used.

```sh
nice -n 19 env TsDotnetRoot=/home/mariogk/Projects/TerraScale/ts-dotnet make terrabase-build CONFIGURATION=Release
(cd benchmark && nice -n 19 go build -trimpath -o bin/repl-local ./cmd/repl-local)
nice -n 19 env TS_HEAVY_HOST=mariogk-dev benchmark/bin/repl-local --scenario second-region --docs 1000 --repeats 3 --out benchmark/bench/2026-10-23-second-region-raw
```

Two independent authenticated test-server OS processes, gru/sjc, each with its own real SQLite files. gRPC peer traffic crosses byte-counting loopback TCP proxies. Test-only membership stands in for ts-orch discovery; test-issued credentials/control files stay in private temporary directories. The reused bootstrap also places fra, but there is no fra process or alternate confirming region. No production knob, feature flag, storage-format, workflow or replication implementation change.

Each fresh cluster receives 1,000 unique individual PUTs from gru at concurrency 16, deterministic seed 20261022, body mix 256 B 70%, 1 KiB 25%, 8 KiB 5%. Exact ID/content hash verification found 1,000/1,000 on both replicas in each repeat: zero missing, extra, mismatched, unknown, failed or retried load writes. Separate low-rate visibility probes write from both regions.

## Measured healthy commits

All times are milliseconds. Per-repeat percentiles are retained; histograms are not pooled and subtracting their percentiles would not measure incremental ACK cost.

| Repeat | docs/s | Local commit p50 / p99 | Full HTTP ACK p50 / p99 | gru→sjc visible p99 (n) | sjc→gru visible p99 (n) |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1 | 76.78 | 94.30 / 458.20 | 195.71 / 634.37 | 324.86 (54) | 273.66 (54) |
| 2 | 63.30 | 99.30 / 442.80 | 195.84 / 899.58 | 889.34 (53) | 515.84 (60) |
| 3 | 43.29 | 158.20 / 536.20 | 301.57 / 858.11 | 953.34 (63) | 808.96 (84) |

Local commit timing is server request dispatch through entry to the real remote wait after origin commit, at 0.1 ms histogram resolution; it includes admission/queueing and excludes the remote wait. Its sample counts include the gru visibility writes. HTTP ACK timing includes client transport, admission, origin commit, remote durable application, ACK and response. Explicit single-send probes from **both** regions required `201` plus `durability:"remote_durable"` and matching values on both replicas.

Visibility is client dispatch to first matching remote GET, with 10 ms polling plus GET latency, and can precede the HTTP ACK. It includes origin work, so this upper bound cannot isolate commit-to-apply lag or demonstrate that internal lag itself exceeds 200 ms. It does not establish the required p99 <200 ms guarantee; retain the performance/release blocker. Raw sample counts, p50/p95/p99/max, local histogram summaries, exact digests, runtime scrapes and host-load samples are in [the raw evidence](2026-10-23-second-region-raw.json).

## Measured partition tradeoffs

After healthy probes, the test-host-only gate holds the matching receiver ACK **after real apply commits**. Both TCP proxies are disconnected while the PUT is still pending. Each fault probe is sent exactly once; generic 5xx retry logic is bypassed.

| Repeat | Partition→unknown response ms | New-write refusal gru / sjc ms | Heal→new remote-durable success ms |
| --- | ---: | ---: | ---: |
| 1 | 43.77 | 10.70 / 4.13 | 1000.98 |
| 2 | 80.21 | 6.34 / 5.63 | 976.50 |
| 3 | 66.06 | 6.06 / 7.00 | 1240.51 |

Every repeat asserted: the already-committed PUT returned `503 write_outcome_unknown`, no Retry-After and no durability claim, with its exact value readable on both isolated replicas. Each isolated region then returned `503 remote_region_unavailable` with Retry-After for a fresh PUT; that ID was absent on both replicas. Reconnecting restored an explicitly remote-durable new write. Only typed pre-execution refusals are retried during healing; ambiguous outcomes are never replayed by this proof.

The tradeoff is visible: a partition prevents new successful writes; a committed write can return an ambiguous client outcome even when both copies exist. This provides neither global serializability nor a backup-commit guarantee. All workers exited successfully; one proxy reset was classified by the existing harness as expected teardown traffic after repeat 2.

## Implementation and executed evidence

| Barrier | Existing implementation and executed integration evidence |
| --- | --- |
| Origin durability | [SQLite writer](../../src/TerraBase/Engine/Sqlite/SqliteDatabaseStore.cs) commits with FULL. [RemoteAckE2ETests](../../tests/TerraBase.E2E/RemoteAckE2ETests.cs) passed all 3 cases: immediate origin SIGKILL, remote restart on its pinned disk, and recovery of the origin CDC log to a region that missed confirmed writes. |
| Remote durable apply | [Receiver](../../src/TerraBase/Features/Replication/PeerStreamReceiver.cs) ACK follows real sink application. [ApplyDurabilityTests](../../tests/TerraBase.Fast/Replication/ApplyDurabilityTests.cs) passed: both receiver apply transactions reported FULL (`2, 2`) before ACK. |
| Truthful client response | [DocumentService](../../src/TerraBase/Features/Documents/DocumentService.cs) releases its writer then waits for its own marker through [PeerStreams](../../src/TerraBase/Features/Replication/PeerStreams.cs). All 39 [RemoteAckTests](../../tests/TerraBase.Fast/Replication/RemoteAckTests.cs) passed, including held/lost/refused ACK, cancellation, missing marker, stale incarnation and pre-execution refusal. |
| Partition | All 3 new process-harness repeats passed the assertions above; no production networking or resources changed. |
| Leaderless/LWW | Both regions accepted remote-durable writes; LWW implementation is unchanged. The existing [three-region overwrite test](../../tests/TerraBase.E2E/ThreeRegionE2ETests.cs), `Burst_of_overwrites_from_three_regions_converges_to_a_last_write`, also passed locally (120 concurrent overwrites, final winner is one region's last write). A second-region copy does not create a global transaction order. |

Executed commands used `nice -n 19 dotnet run --configuration Release --no-build` with project Fast and separate filters `/*/*/RemoteAckTests/*` (39) and `/*/*/ApplyDurabilityTests/*` (1), and project E2E filter `/*/*/RemoteAckE2ETests/*` (3). The additional E2E LWW filter `/*/*/ThreeRegionE2ETests/Burst_of_overwrites_from_three_regions_converges_to_a_last_write` also passed (1); `env -u TERRABASE_E2E_ORCH_NODES` forced its local in-process cluster. All 44 selected cases passed with zero skips. Release build passed with zero warnings/errors; focused Go packages `cmd/repl-local`, `internal/repllocal`, `internal/replbench` passed. [Test results and binary SHA-256](2026-10-23-second-region-tests.json) retain individual test identities. These are affected checks; full required CI remains the merge gate.

## Production boundary and remaining gates

Read-only GET `https://gru.api.terrascale.tech/healthz` on 2026-10-23 returned HTTP 200: status ok, version 0.1.0, region gru, revision `888ea413c06f6452cccceab57c735fa35b4d850d`, storageFormat `11-022325879f76`, machine `gru-1-ctl-1`. This identifies one live artifact, not the measured harness/source revision or a remote serving copy.

The clean [ts-orch deployment source](https://github.com/TerraScale/ts-orchestrator/blob/1ed9883126b0c4c4635b0ca9df9fdef8fd4bc4ee/deploy/m1/services/terrabase.json) declares only `["gru"]`, image digest `509165626e0ef74e10a711e4bd1be61b35e4ea238f1e3b8780ff1b7afe509a7e` (manifest SHA-256 `fc87d60487fc935d405090d40ed4993c547699d67e15d8e037f7e303081301bb`). No deployed second-region barrier is claimed. No deployment change was made.

This local proof does not verify OCI mesh/WAN behavior, region failure independence, physical power loss/fsync hardware guarantees, backup persistence or the running artifact's full contract. Preserve current-head CI/review, performance and deployment/release blockers. Parent owns review, merge and ticket completion. Two development attempts (invalid prefixed marker ID; uppercase proof collection name) failed before the final run; both harness issues were corrected and those samples are excluded.
