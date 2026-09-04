# Benchmarks (E-13)

One command per family appends here; this file is generated, never
hand-edited. Each family section is delimited by markers so sibling
families render independently. A number here is a fact about the host and
moment that produced it, per the limits each results document carries.

## Resource ceiling (all families, owner decision 2026-08-31)

Every MEASURED subject — the TerraBase member harness, PostgreSQL /
pgbouncer / pgrest, RavenDB — runs capped at **8 vCPU / 8 GiB RAM**
(`bench/scripts/resource-envelope.sh`, spec `8cpu/8GiB`: `taskset -c 0-7`
CPU affinity inside a `systemd-run` scope holding the memory ceiling).
Load generators (the bench lane process) are NOT capped: they are the
instrument, not the subject. Disk is unfenced; write-family numbers must
be read next to the ambient `io-pressure` snapshot the runner records.
`TERRABASE_BENCH_ENVELOPE=0` opts a run out of the envelope for diagnosis;
such runs are stamped unfenced and never join a comparison.

## Families

| family | workloads | status |
| --- | --- | --- |
| point ops | read, write | rendered below |
| indexed query | query | <!-- E-13 indexed-query STATUS -->pending<!-- E-13 indexed-query STATUS-END --> |
| ingest | bulk load | <!-- E-13 ingest STATUS -->pending<!-- E-13 ingest STATUS-END --> |
| auto-indexing | index build / shadow verify | <!-- E-13 auto-indexing STATUS -->pending<!-- E-13 auto-indexing STATUS-END --> |
| sync | view attach / delta log | <!-- E-13 sync STATUS -->pending<!-- E-13 sync STATUS-END --> |


<!-- E-13 point-ops BEGIN -->

### Point ops (read / write)

Point read by id and acknowledged create/replace by id, one
connection per concurrency slot, warm-up excluded from every
measured sample. `query` lives in the indexed-query family;
`mixed` is a cross-family composite rendered by no family here.
`diagnostic` = diagnostic-boundary trajectory (no comparability
contract, never decision-grade); `failed` = newest retained
attempt failed, older complete attempts are superseded.

| workload | side | status | ops/s | p50 us | p95 us | p99 us | wall ms |
| --- | --- | --- | --- | --- | --- | --- | --- |
| read | terrabase | diagnostic | 2651 | 2985 | 24259 | 49755 | 3772 |
| read | postgresql | failed | - | - | - | - | - |
| read | pg-fullstack | complete | 9873 | 1391 | 3021 | 4410 | 1013 |
| read | ravendb | complete | 13330 | 600 | 3714 | 15195 | 750 |
| write | terrabase | missing | - | - | - | - | - |
| write | postgresql | failed | - | - | - | - | - |
| write | pg-fullstack | complete | 1818 | 7556 | 15281 | 24278 | 5501 |
| write | ravendb | complete | 1470 | 7982 | 23598 | 72277 | 6802 |

<!-- E-13 point-ops END -->



