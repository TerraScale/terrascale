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
| Read | Complete diagnostic; current cohort | 1,744.589 | 8,886.641 | 13,758.746 | 18,382.490 | [receipt](results/run-20260907-independent-1714381/final/terrabase-read-1788833575687752642-1977542-0.json) |
| Write | Failed; measured operation 5,631 timed out | — | — | — | — | [receipt](results/run-20260907-independent-1714381/final/terrabase-write-1788833630322882176-1984173-0.json) |
| Query | Failed; measured operation 11,306 timed out | — | — | — | — | [receipt](results/run-20260907-independent-1714381/final/terrabase-query-1788833703538524482-1987194-0.json) |
| Mixed | Not started; driver stopped after query failure | — | — | — | — | [run directory](results/run-20260907-independent-1714381/final/) |

The final cohort read completed five samples using the pinned `fae32` server
artifact and the required 2 OCPU / 12 GiB envelope. It remains diagnostic until
all requested local lanes complete and the full evidence set is reviewed.

The same cohort's write and query lanes failed during measurement at operations
5,631 and 11,306 respectively; both retained zero samples because the lane
requires a complete run. The driver then stopped before mixed. Earlier attempts
in this run directory had separate warm-up 429/503/timeout failures and are not
merged with this final cohort.

## Final cohort attempt ledger

| Cohort | Read | Write | Query | Mixed | Artifact |
| --- | --- | --- | --- | --- | --- |
| `e13-local-20260908T0212Z` | Complete (5 samples) | Failed at op 5,631 | Failed at op 11,306 | Not started | `f1aa1737…` |
| Earlier attempts | Failed before samples | Failed before samples | Failed before samples | Failed before samples | prior/diagnostic artifacts |

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
| Read | Not qualified | 1,744.589 ops/s diagnostic | Undefined: historical artifact/schema identity differs |
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
