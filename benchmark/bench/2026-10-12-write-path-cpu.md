# Write path CPU and allocations, single region, before and after (2026-10-12)

Profile-driven optimization of the TerraBase write path, without touching any replication slice in flight.

- Host: beta (12 vCPU, 14 GB RAM, otherwise idle). One region (gru) in the benchmark host process, Release build, main at
  `5e88fc07` plus the patch. Nothing replicates between R2a-1 and R4a, so this is the single-node path every region runs.
- Load: `TERRABASE_BENCH_REGIONS=gru make benchmark-repl-local REPL_ARGS='--docs 120000 --batch 1'`, which means one
  PUT per document, 16 writers, sizes 256 B 70 % / 1 KiB 25 % / 8 KiB 5 %, 1 % deletes, conflict patches and probes.
- Counters: `dotnet-counters collect --counters System.Runtime` for 30 s inside the load, three runs per variant. The
  profile came from `dotnet-trace collect` (CPU samples, then allocation ticks with stacks).

## What the profile showed (before)

The process used 2.6 of 12 cores at about 1,550 documents/s. Throughput is bound by the write lane's commits, not CPU.
CPU and allocations still decide how much a 2-vCPU, 512 MB machine can take.

| Hotspot (share of CPU samples, excluding waits) | Share | Fix in this change |
| --- | ---: | --- |
| JWT validation (`JsonWebTokenHandler.ValidateTokenAsync`, the ECDSA signature check 11.7 %) | 12.5 % | yes: accepted-token cache |
| `SqliteStorageEngine.OpenSystemDatabaseAsync` re-checking the system database path for links (readlink, lstat) on every request | 3.1 % | yes: only when it opens |
| Request body read: a new 16 KiB chunk, a growing `MemoryStream` and a copy per request (11.6 % of allocated bytes) | n/a | yes: one array of Content-Length |
| SQLite statement prepare and named-parameter binding per command | 2.6 % | no: the R2a write path owns these statements |
| `DocumentState` materialize, canonical form and digest (writer buffers growing, LINQ in `FieldState.Value`) | about 5 % of allocated bytes | no: R1/R2a code; proposed to its owner |
| Catalog reads per request (`EngineManagementService` project, plan) | about 3 % | no: R10a replaces the catalog |

## Before and after

| Variant | Run | docs/s | CPU cores (user + sys) | Allocated MB/s | gen0 GC/s | Request p50 / p99 ms |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| before | 1 | 1,569 | 2.62 | 302.7 | 23.3 | 8.1 / 34.2 |
| before | 2 | 1,537 | 2.59 | 296.0 | 24.3 | 8.2 / 35.4 |
| before | 3 | 1,536 | 2.65 | 298.4 | 22.7 | 8.3 / 34.5 |
| body read + system DB check | 1 | 1,553 | 2.48 | 260.2 | 20.6 | 8.2 / 34.4 |
| body read + system DB check | 2 | 1,569 | 2.49 | 257.0 | 21.6 | 8.1 / 34.3 |
| body read + system DB check | 3 | 1,540 | 2.48 | 254.4 | 21.6 | 8.2 / 34.9 |
| all three | 1 | 1,585 | 2.27 | 252.5 | 20.5 | 8.0 / 33.7 |
| all three | 2 | 1,596 | 2.29 | 254.4 | 21.8 | 8.0 / 33.8 |
| all three | 3 | 1,582 | 2.26 | 250.9 | 21.8 | 8.0 / 34.4 |

Means per request (requests/s ≈ docs/s):

| | CPU per request | Allocated per request | Throughput |
| --- | ---: | ---: | ---: |
| before | 1.69 ms | 193 KB | 1,547 docs/s |
| after | 1.43 ms (−15 %) | 159 KB (−18 %) | 1,588 docs/s (+2.6 %) |

## Not measured

- Cloud machines. A 2-vCPU machine is CPU-bound much sooner, where the 15 % should matter more, but that has not been measured.
- Requests authenticated by API key rather than a ts-auth JWT: the token cache does not apply to them.
- Clients that mint a new token per request: they get no cache hits.
- Replication: out of scope until R4a.
