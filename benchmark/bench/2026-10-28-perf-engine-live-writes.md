# Live c=1 writes in gru, and where the gap comes from, 2026-10-28

## Live gru, image of #845 (`a53ad20e41bf`, main `1035dd682`)

- **Driver:** `benchmark/scripts/perf-live-writes.py`, launched through `perf-live-writes.sh`, a link to `perf-live.sh`
  that runs the driver named after itself.
- **Tenant:** a synthetic RT6 tenant (organization, project, an all-regions database and a documents key).
- **Writes:** 16 documents per size, then 60 c=1 PUTs per cell, paced at most 10/s, replacing them in turn.
  - HTTP goes to the host-local client listener (127.0.0.1:8080, persistent connection).
  - Wire goes over the local TLS edge (7464). Both are `local_durable`.
- **Repeats and load:** three repeats. Host load 0.4–2.7 of 4 vCPU, IO PSI some avg60 3%.
- **sjc:** it served another, unmatched image (`5cfbb44af36e`) and was briefly unready during the run. Its internal
  listener refused the teardown's database delete.
- **Cleanup:** `perf-live.sh … cleanup` deleted the database, project and organization once sjc was ready. Every one
  read 404 in gru, sjc and fra afterwards; the key was revoked during the run.
- **fra:** the same driver could not create its database (`409 database_limit`: allowance held in regions it could not
  reach). Its organization and project were deleted and confirmed gone everywhere.

| Size | Surface | p50 ms per repeat | p99 ms per repeat | median p50 ms |
| --- | --- | --- | --- | ---: |
| small | HTTP PUT | 98.7, 96.5, 77.9 | 499.4, 315.1, 187.2 | 96.5 |
| small | Wire put | 94.7, 90.0, 67.7 | 306.7, 312.8, 288.6 | 90.0 |
| medium | HTTP PUT | 94.0, 93.6, 20.8 | 204.2, 502.6, 385.3 | 93.6 |
| medium | Wire put | 90.5, 94.3, 67.2 | 273.2, 509.5, 118.9 | 90.5 |

These are after-numbers only: #845 was live before a live baseline existed.

- **Reads by comparison:** P1's live reads on the same host type are fast. gru documents p50 is 1.7 ms and wire 1.0 ms
  (`perf-baseline-20261027/live-summary.md`), but their p99s cluster at 78–90 ms.
- **Disk:** a 4 KiB `dd oflag=dsync` takes about 2.1 ms on gru's volume and 2.6 ms on fra's.
- **Directory scan:** the storage-quota scan of the data directory on gru (18 organization and 146 project directories)
  takes about 14 ms in total.

## Local reproduction: a three-region cell, delta

`benchmark/dotnet/VsTurso`, enterprise plan, three repeats, c=1 `local_durable` writes in both lanes:
- `engine` is one node;
- `engine-3` is gru of an in-process gru/sjc/fra cluster.

| Size | Lane | Ops/s per repeat | p50 µs per repeat | CPU µs/op per repeat |
| --- | --- | --- | --- | --- |
| small | engine | 557, 492, 465 | 1,427, 1,706, 1,503 | 1,457, 1,818, 1,469 |
| small | engine-3 | 45, 34, 21 | 20,144, 33,396, 34,268 | 30,729, 29,901, 31,691 |
| medium | engine | 336, 460, 114 | 1,600, 1,816, 2,478 | 1,822, 1,855, 2,240 |
| medium | engine-3 | 43, 40, 17 | 21,293, 22,954, 20,124 | 29,920, 33,251, 29,391 |

The three nodes share delta's 16 vCPUs, so the CPU per write is the whole cluster's.

- **`perf` profile:** one more `engine-3` small/medium run (20 s cells, sudo `perf record -g`).
- **Client write:** of the Turso threads' samples, the write itself (`TursoStorageTransaction.Put`) is about 11%.
- **Replication:** around it, on the same threads:
  - `Join` (peers applying the change), 27%;
  - `ReadChangeGroups` / `DecodeChangeGroups`, 18%;
  - `TursoChangeGroupSource`'s `ReadShippableAsync`, 13%;
  - the tailer's `TailTransaction.Add`, 11%.
- **Leaf frames:** native leaves are overflow-page reads (`process_overflow_read` 8.5%), column fetches and reverse
  b-tree scans.
- **Ownership:** these files belong to the replication lane (P3). This report hands them over rather than changing them.

## Paid plans lose group commit at c=16, delta

The same benchmark, with the plan changed only. Two repeats each, writes.

| Size | Lane | c | Enterprise ops/s | Pro ops/s | Enterprise p50 µs | Pro p50 µs | CPU µs/op enterprise → pro |
| --- | --- | ---: | --- | --- | --- | --- | --- |
| small | engine | 1 | 498, 474 | 522, 477 | 1,681, 1,899 | 1,523, 1,780 | 1,705 → 1,585 |
| small | engine | 16 | 2,077, 2,079 | 512, 430 | 6,197, 6,359 | 28,276, 37,179 | 431 → 1,640 |
| small | wire | 16 | 1,934, 1,987 | 459, 480 | 6,722, 6,492 | 34,641, 31,624 | 546 → 2,076 |
| medium | engine | 16 | 1,532, 1,674 | 497, 467 | 8,160, 7,416 | 30,165, 32,853 | 575 → 1,699 |

`StorageQuota.EnterAsync` holds a per-organization gate from before a write to after its commit, for every plan with a
storage limit. Its comment states that enterprise skips it "to keep engine group commit". Concurrent writes of one
paying organization therefore commit one at a time.
