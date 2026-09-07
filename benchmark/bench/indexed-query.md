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
