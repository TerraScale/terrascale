# Benchmarks (E-13)

One command per family appends here; this file is generated, never
hand-edited. Each family section is delimited by markers so sibling
families render independently. A number here is a fact about the host and
moment that produced it, per the limits each results document carries.

## Resource ceiling (all families, owner decision 2026-08-31)

Every MEASURED launch member — the TerraBase member, PostgreSQL /
pgbouncer / pgrest, RavenDB — runs capped at **2 OCPU / 12 GiB RAM**
(`bench/scripts/resource-envelope.sh`, spec `2ocpu/12GiB`: two-CPU
affinity inside a `systemd-run` scope holding the memory ceiling).
Load generators (the bench lane process) are NOT capped: they are the
instrument, not the subject. Disk is unfenced; write-family numbers must
be read next to the ambient `io-pressure` snapshot the runner records.
Runs without verified cgroup evidence are unavailable and never join a
comparison. There is no unfenced gating mode.

## Families

| family | workloads | status |
| --- | --- | --- |
| point ops | read, write | rendered below |
| indexed query | query | <!-- E-13 indexed-query STATUS -->failed<!-- E-13 indexed-query STATUS-END --> |
| ingest | bulk load | <!-- E-13 ingest STATUS -->unavailable<!-- E-13 ingest STATUS-END --> |
| auto-indexing | index build / shadow verify | <!-- E-13 auto-indexing STATUS -->unavailable<!-- E-13 auto-indexing STATUS-END --> |
| sync | view attach / delta log | <!-- E-13 sync STATUS -->unavailable<!-- E-13 sync STATUS-END --> |


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
| read | terrabase | failed | - | - | - | - | - |
| read | postgresql | failed | - | - | - | - | - |
| read | pg-fullstack | invalid | - | - | - | - | - |
| read | ravendb | invalid | - | - | - | - | - |
| write | terrabase | failed | - | - | - | - | - |
| write | postgresql | failed | - | - | - | - | - |
| write | pg-fullstack | invalid | - | - | - | - | - |
| write | ravendb | invalid | - | - | - | - | - |

<!-- E-13 point-ops END -->



<!-- E-13 benchmark-history BEGIN -->

### Benchmark history

Every row is derived from a lane results document under `bench/results/`
by `terrabase-bench-lane --render-history` — never hand-edited. Verdicts and
receipts are produced only by the engine-side adapter
(`src/benchmarks/`); this table tabulates facts. A wall-clock number here
is a fact about the host and moment that produced it, per the limits each
results document carries.

Lane configuration: seed 1,000 documents · 10,000 operations per sample ·
5 samples · concurrency 16 · 1,000 warm-up operations · 5% noise band,
per `LaneConfig::RECORD_MINIMUM`.

## Sides


## Admission and comparability

Only rows with a valid comparability contract may join a workload comparison; no verdict is produced by this renderer. Failed/raw attempts remain retained and never contribute measurements.

- terrabase/read: failed/raw — `bench/results/terrabase-read-1788816291301969015-821228-0.json` — legacy Go served benchmark adapter is retired; use a direct public-boundary family
- postgresql/read: failed/raw — `bench/results/postgresql-read-1788137614741020195-272364-0.json` — cannot connect to the pinned reference: error connecting to server
- pg-fullstack/read: invalid/raw — `bench/results/pg-fullstack-read-1788187005634601753-306703-0.json` — comparability.run_digest is not a SHA-256 digest
- ravendb/read: invalid/raw — `bench/results/ravendb-read-1788135679661528717-4010795-0.json` — comparability.run_digest is not a SHA-256 digest
- terrabase/write: failed/raw — `bench/results/terrabase-write-1788816291307716735-821233-0.json` — legacy Go served benchmark adapter is retired; use a direct public-boundary family
- postgresql/write: failed/raw — `bench/results/postgresql-write-1788137614935028280-272475-0.json` — cannot connect to the pinned reference: error connecting to server
- pg-fullstack/write: invalid/raw — `bench/results/pg-fullstack-write-1788187050721866209-310755-0.json` — comparability.run_digest is not a SHA-256 digest
- ravendb/write: invalid/raw — `bench/results/ravendb-write-1788135716237245380-4013312-0.json` — comparability.run_digest is not a SHA-256 digest
- postgresql/query: failed/raw — `bench/results/postgresql-query-1788137615123935909-272552-0.json` — cannot connect to the pinned reference: error connecting to server
- pg-fullstack/query: invalid/raw — `bench/results/pg-fullstack-query-1788187017599762987-309854-0.json` — comparability.run_digest is not a SHA-256 digest
- ravendb/query: invalid/raw — `bench/results/ravendb-query-1788135744820433018-4027234-0.json` — comparability.run_digest is not a SHA-256 digest
- postgresql/mixed: failed/raw — `bench/results/postgresql-mixed-1788137615313202308-272578-0.json` — cannot connect to the pinned reference: error connecting to server
- pg-fullstack/mixed: invalid/raw — `bench/results/pg-fullstack-mixed-1788187073308144008-328055-0.json` — comparability.run_digest is not a SHA-256 digest
- ravendb/mixed: invalid/raw — `bench/results/ravendb-mixed-1788135763958015648-4034105-0.json` — comparability.run_digest is not a SHA-256 digest
- read: no complete comparison group
- write: no complete comparison group
- query: no complete comparison group
- mixed: no complete comparison group

## Retained failed/raw attempts

These attempts are retained for audit and are never used as complete measurements.

- `bench/results/auto-index-1788816336-828597.json` — missing required TERRABASE_BENCH_TERRABASE_ENDPOINT
- `bench/results/postgresql-mixed-1788135184461262373-3832326-0.json` — cannot connect to the pinned reference: error connecting to server
- `bench/results/postgresql-mixed-1788137615313202308-272578-0.json` — cannot connect to the pinned reference: error connecting to server
- `bench/results/postgresql-query-1788135183790116747-3831866-0.json` — cannot connect to the pinned reference: error connecting to server
- `bench/results/postgresql-query-1788137615123935909-272552-0.json` — cannot connect to the pinned reference: error connecting to server
- `bench/results/postgresql-read-1788135180812017910-3786857-0.json` — cannot connect to the pinned reference: error connecting to server
- `bench/results/postgresql-read-1788137614741020195-272364-0.json` — cannot connect to the pinned reference: error connecting to server
- `bench/results/postgresql-write-1788135182670212210-3831205-0.json` — cannot connect to the pinned reference: error connecting to server
- `bench/results/postgresql-write-1788137614935028280-272475-0.json` — cannot connect to the pinned reference: error connecting to server
- `bench/results/terrabase-read-1788127672017788122-manual-0.json` — qualification harness exited before readiness: the TBEP serving policy is outside its bounds because the supplied gateway principal did not satisfy the production SPIFFE identity grammar
- `bench/results/terrabase-read-1788127825772532192-2666279-0.json` — bind seed client: rpc error: code = Unavailable desc = connection error: desc = "transport: authentication handshake failed: credentials: cannot check peer: missing selected ALPN property. If you upgraded from a grpc-go version earlier than 1.67, your TLS connections may have stopped working due to ALPN enforcement. For more details, see: https://github.com/grpc/grpc-go/issues/434"
- `bench/results/terrabase-read-1788128076667027110-manual-0.json` — qualification harness exited before readiness: engine open refused
- `bench/results/terrabase-read-1788128658931014378-1.json` — seed operation 0: terrabase: usage_unavailable (grpc FailedPrecondition): a production network mutation requires trusted usage admission
- `bench/results/terrabase-read-1788128659735477939-2.json` — Go benchmark adapter exited with status exit status: 1
- `bench/results/terrabase-read-1788128992385733234-2.json` — invalid adapter result: config operation counts must be divisible by concurrency
- `bench/results/terrabase-read-1788816291301969015-821228-0.json` — legacy Go served benchmark adapter is retired; use a direct public-boundary family
- `bench/results/terrabase-write-1788816291307716735-821233-0.json` — legacy Go served benchmark adapter is retired; use a direct public-boundary family

## Results (median across samples)

| workload | side | status | ops/s | p50 µs (median–max) | p95 µs | p99 µs | sample wall ms |
| --- | --- | --- | --- | --- | --- | --- | --- |
| read | terrabase | failed | — | — | — | — | — |
| read | postgresql | failed | — | — | — | — | — |
| read | pg-fullstack | invalid | — | — | — | — | — |
| read | ravendb | invalid | — | — | — | — | — |
| write | terrabase | failed | — | — | — | — | — |
| write | postgresql | failed | — | — | — | — | — |
| write | pg-fullstack | invalid | — | — | — | — | — |
| write | ravendb | invalid | — | — | — | — | — |
| query | terrabase | missing | — | — | — | — | — |
| query | postgresql | failed | — | — | — | — | — |
| query | pg-fullstack | invalid | — | — | — | — | — |
| query | ravendb | invalid | — | — | — | — | — |
| mixed | terrabase | missing | — | — | — | — | — |
| mixed | postgresql | failed | — | — | — | — | — |
| mixed | pg-fullstack | invalid | — | — | — | — | — |
| mixed | ravendb | invalid | — | — | — | — | — |

<!-- E-13 benchmark-history END -->


<!-- E-13 indexed-query BEGIN -->

# E-13 indexed query (e-13-indexed-query-production-v1)

Status: **failed - fail-closed**  
Topology: unknown (single-node and RF=2 results are separate comparison groups)  
Boundary: production-member-unknown (TerraBase served member)  
Launch member resource evidence: **2 OCPU / 12 GiB RAM**  
Admission error: TERRABASE_BENCH_INDEXED_QUERY_RUN_DIGEST is required

| side | status | p50 us | p95 us | p99 us | rows returned | rows examined | pages read | logical work |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| terrabase | failed - not launched: OCI launch-member resource evidence file is missing | - | - | - | - | - | - | - |
| postgresql-jsonb | failed - adapter unavailable (TERRABASE_BENCH_INDEXED_QUERY_POSTGRESQL_JSONB_CMD is not set) | - | - | - | - | - | - | - |
| ravendb | failed - adapter unavailable (TERRABASE_BENCH_INDEXED_QUERY_RAVENDB_CMD is not set) | - | - | - | - | - | - | - |

Numbers are admitted only from side adapters with observed identities, immutable image digests, five or more samples, and logical_work = pages_read + rows_examined. Missing comparators and the served TerraBase member remain failed/unavailable and cannot support a performance claim.

<!-- E-13 indexed-query END -->


<!-- E-13 ingest BEGIN -->

### Bulk ingest

| target | topology | status | documents/sample | docs/s | p50 batch us | p95 batch us | p99 batch us |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |
| terrabase | single-node | unavailable | - | - | - | - | - |
| terrabase | rf2 | unavailable | - | - | - | - | - |
| postgresql-jsonb | single-node | unavailable | - | - | - | - | - |
| postgresql-jsonb | rf2 | unavailable | - | - | - | - | - |
| ravendb | single-node | unavailable | - | - | - | - | - |
| ravendb | rf2 | unavailable | - | - | - | - | - |

The launch-member contract is exactly 2 OCPU / 12 GiB; single-node and RF=2 rows never share a comparison group.

<!-- E-13 ingest END -->


<!-- E-13 auto-indexing BEGIN -->

### Auto-indexing

Status: **failed** · topology: **unknown** · documents: **-** · receipt: `bench/results/auto-index-1788816336-828597.json`

| phase | subject | value |
| --- | --- | --- |

Required gates: 5% regression rollback, fixed-point replay, 12 visible-index cap, and no more than 2× no-index write maintenance cost.

<!-- E-13 auto-indexing END -->


<!-- E-13 sync BEGIN -->

### Sync

Status: **unavailable** — no served-boundary receipt was retained.

| topology | lane | status | p50 ms | p99 ms | samples |
| --- | --- | --- | ---: | ---: | ---: |
| unknown | local_websocket | unavailable | - | - | - |
| unknown | cdn_http | unavailable | - | - | - |
| unknown | cross_region | unavailable | - | - | - |

<!-- E-13 sync END -->

