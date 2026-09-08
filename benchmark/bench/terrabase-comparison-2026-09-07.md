# TerraBase benchmark comparison — 2026-09-07

This report contains TerraBase-only benchmark evidence. Existing benchmark
reports and raw receipts are preserved. A row is numerical only when its
receipt completed and the workload, boundary, topology, resource envelope,
durability, and artifact identities are comparable.

## Standard point-operation contract

| Seeds | Operations/sample | Samples | Concurrency | Warmup | Noise band | Boundary |
| ---: | ---: | ---: | ---: | ---: | ---: | --- |
| 1,000 | 10,000 | 5 | 16 | 1,000 | 5% | served public API |

All measured members must use the 2 OCPU / 12 GiB resource envelope. Throughput
is operations per second; latency is microseconds. Failed attempts have no
throughput and are shown as failed rather than zero.

## Current TerraBase results

| Workload | Status | Ops/s | p50 (µs) | p95 (µs) | p99 (µs) | Receipt |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| Read | Pending current-source receipt | — | — | — | — | [results](results/) |
| Write | Pending current-source receipt | — | — | — | — | [results](results/) |
| Query | Pending current-source receipt | — | — | — | — | [results](results/) |
| Mixed | Pending current-source receipt | — | — | — | — | [results](results/) |

The latest complete read attempt in `results/run-20260907-independent-1714381/`
reported 1,839.6 ops/s (p50 8,448.0 us, p95 12,937.8 us, p99 15,378.1 us),
but it reused the prior release binary after the current source failed to
compile. It is retained diagnostic evidence and cannot qualify the current
source or establish an optimization result.

The same attempt's write, query, and mixed lanes failed before samples (503 or
timeout). Those receipts remain audit evidence and contribute no measurements.
The current-source benchmark run is blocked until the small `shard_split`
compile correction is built and exercised.

### Additional families

| Family | Topology/lane | Status | Required comparison facts |
| --- | --- | --- | --- |
| Indexed query | single-node and RF=2 separately | Pending receipt | p50/p95/p99, rows returned/examined, pages read, logical work |
| Bulk ingest | single-node and RF=2 separately | Pending receipt | documents/sample, docs/s, batch p50/p95/p99, resource evidence |
| Auto-indexing | TerraBase local diagnostic | Pending receipt | seed, write cost at 0/5/12 indexes, activation, replay, stale-read checks |
| Sync | local WebSocket, CDN HTTP, cross-region | Pending receipt | attach, snapshot identity, delivery p50/p99, resource evidence |

## Historical diagnostic reference

The retained full read run below is useful for orientation only. It is not a
qualified before/after baseline because the receipt lacks a frozen
source-to-executable/schema comparison and uses a diagnostic boundary.

| Run | Workload | Settings | Status | Ops/s | p50 (µs) | p95 (µs) | p99 (µs) |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: |
| [performance-baseline-2026-09-07](performance-baseline-2026-09-07.md) | Read | 1,000 / 10,000 / 5 / C16 / 1,000 warmups | Complete diagnostic | 1,822.212 | 8,538.314 | 13,093.030 | 15,081.951 |

The historical standard write measurement is missing. The retained eight-write
smoke run and one failed standard write attempt cannot be compared to a
sustained C16 run. Historical query and mixed rows likewise used one sample and
different settings.

## Before/after comparison

| Workload | Historical comparable value | Current value | Change |
| --- | ---: | ---: | ---: |
| Read | Not qualified | Pending current-source receipt | Undefined until matched receipts |
| Write | Not available | Pending | Undefined until matched receipts |
| Query | Not qualified | Pending | Undefined until matched receipts |
| Mixed | Not qualified | Pending | Undefined until matched receipts |

No PostgreSQL or other database result is included: the request scope is
TerraBase only, and the retained comparator audit admits zero usable baselines.

## Identity and evidence checklist

The final rows will link each raw receipt and record candidate/runtime
executable, workload, topology, schema/configuration provenance, controlled host,
resource limits, durability semantics, and memory/I/O snapshots or mark those
facts unavailable. A failed warm-up, malformed response, missing identity, or
unavailable remote deployment remains explicitly failed/unavailable.
