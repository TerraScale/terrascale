# RT6 production endpoint matrix (batches 15 and 16)

Two production runs of [the operator harness](../../scripts/terrabase/endpoint-replication-proof.sh) wrote and read
through all five endpoints (documents, SQL HTTP, Postgres, Hrana, Wire) in gru, sjc and fra.

| Batch | Serving revision | Window (UTC) | Result |
| --- | --- | --- | --- |
| 16 | `d77afebf96ebf1f69b01ecbbeb8fc0fa55cbe111` | 2026-10-26 15:57:58 to 16:08:40 | **PASS** |
| 15 | `f8b0c3270c4ba9aa6b9f2d8418452bbb5f5e822f` | 2026-10-26 14:50:20 to 15:01:41 | **PASS_WITH_GAPS**: Wire DELETE predates #688 |

Internal readiness verified the exact revision in every region at both run boundaries of each run. Storage format:
`18-e15d70682919` in both. Batch 16 includes PR #688 (Wire operation `3`, DELETE) and closes batch 15's only gap.

## Method

The controller ran on ts-sjc-1 using the internal h2c listener and its running container's peer credential in a
temporary mode-600 header file. Each run created a fresh synthetic user (`X-TerraBase-User-Id`) owning an isolated
organization, project, allRegions database and database-scoped native data key. No email, ts-auth sign-in or
client-credential exchange was used.

Public Postgres and Wire ports time out from ts-sjc-1. Six temporary loopback-only SSH reverse forwards from delta
carried native TLS connections to the three regional public IPs. Postgres STARTTLS used `pg.terrascale.tech`; Wire used
`api.terrascale.tech`. System certificate and hostname verification stayed enabled; all six preflight handshakes
negotiated TLS 1.3 before each run. Peer credentials remained on ts-sjc-1. Change-feed origin markers verified that
each native transport wrote in the same region as its internal HTTP control.

Three repeats originated CRUD through every endpoint in each region and observed each mutation through all five
readers in all three regions. Repeat two used immutable database ULIDs for data routes and independently authenticated
Postgres/Wire selectors; the other repeats used names. SQL HTTP, Postgres and Hrana also originated multi-statement
commits and rollback controls. Reads could reconnect within the 30-second observation window and record each read
error; writes were never retried. Acknowledgements used local durability. No failover or destructive production fault
was induced.

## Functional results

| Check | Batch 16 | Batch 15 |
| --- | --- | --- |
| CRUD and committed transactions, five readers × three regions | 2835/2835 | 2700/2700 |
| Wire DELETE writer cells (3 regions × 3 repeats) | 9/9, 135/135 observations | 0/9, blocked `400 unknown_operation` |
| LWW conflict races with exact maximum (version, origin) oracle | 15/15 | 15/15 |
| LWW winner and second stability pass | 450/450 | 450/450 |
| Causal seeds before conflict races | 225/225 | 225/225 |
| Rollback absence | 405/405 | 405/405 |
| Committed sentinels after rollback | 405/405 | 405/405 |
| Name/ULID SQL and native selector probes | 18/18 | 18/18 |
| HTTP/Postgres/Wire regional origin controls | 3/3 | 3/3 |
| Observation mismatches | 0 | 0 |
| Run failures | 0 | 0 |
| Retried transient read errors | 0 | 2 |

| Writer | Batch 16 observations | Batch 15 observations |
| --- | --- | --- |
| documents | 405/405 | 405/405 |
| sql | 675/675 | 675/675 |
| postgres | 675/675 | 675/675 |
| hrana | 675/675 | 675/675 |
| wire | 405/405 | 270/270 |

ALTER TABLE was refused identically by both builds; no writer accepted it:

| ALTER writer | Accepted | Refusal |
| --- | --- | --- |
| sql | False | 400 `sql_error`: this statement is not supported (`SQLITE_ERROR`) |
| postgres | False | `0A000`: this statement is not supported |
| hrana | False | 200 with `type: error`: this statement is not supported (`SQLITE_ERROR`) |

These probes record the serving build's response; they do not prove metadata replication for a future ADD COLUMN
feature.

## Acknowledgement-to-observation times

Times include client scheduling, curl process costs, WAN traversal, TLS connection establishment when needed and the
SSH relay for native readers. They measure completion of the first matching read after acknowledgement. These
observations cannot isolate engine replication latency or support a 200 ms replication claim. Nearest-rank
percentiles over cross-region CRUD/commit observations; each row summarizes its own samples.

| Batch 16 reader | N | p50 ms | p99 ms | max ms |
| --- | ---: | ---: | ---: | ---: |
| documents | 378 | 363.396 | 3267.003 | 3707.571 |
| sql | 378 | 365.326 | 3292.263 | 3706.094 |
| postgres | 378 | 603.297 | 5055.326 | 5809.415 |
| hrana | 378 | 367.546 | 3284.662 | 3711.102 |
| wire | 378 | 597.241 | 4563.011 | 5149.471 |

| Batch 15 reader | N | p50 ms | p99 ms | max ms |
| --- | ---: | ---: | ---: | ---: |
| documents | 360 | 363.481 | 3447.883 | 3775.420 |
| sql | 360 | 366.312 | 3448.045 | 3774.883 |
| postgres | 360 | 621.735 | 4532.291 | 7737.730 |
| hrana | 360 | 372.977 | 3450.601 | 3784.766 |
| wire | 360 | 615.802 | 4009.947 | 5234.489 |

| Batch | Repeat | N | p50 ms | p99 ms | max ms |
| --- | --- | ---: | ---: | ---: | ---: |
| 16 | 1 | 630 | 429.674 | 3707.571 | 5149.471 |
| 16 | 2 | 630 | 426.578 | 4459.808 | 5809.415 |
| 16 | 3 | 630 | 396.890 | 2176.622 | 2485.960 |
| 15 | 1 | 600 | 431.456 | 3448.045 | 4969.776 |
| 15 | 2 | 600 | 419.176 | 4009.947 | 5690.040 |
| 15 | 3 | 600 | 420.162 | 2668.313 | 7737.730 |

Batch 16 per-repeat p50: median 426.578 ms, spread 396.890 to 429.674 ms. Batch 15: median 420.162 ms, spread
419.176 to 431.456 ms. Same controller host, relay path and workload in both batches.

## Cleanup and evidence

Both runs removed their tenant:

| Step | Region | Batch 16 | Batch 15 |
| --- | --- | --- | --- |
| revoke key | sjc | 204 | 204 |
| delete database | sjc | 204 | 204 |
| delete project | sjc | 204 | 204 |
| delete organization | sjc | 204 | 204 |
| project gone | gru | 404 | 404 |
| project gone | sjc | 404 | 404 |
| project gone | fra | 404 | 404 |

The SSH relays and their port map were removed after each run.

Raw evidence: [batch 16 JSONL](rt6-endpoint-matrix-20261026/batch16/20261026T155758Z.jsonl) and
[batch 15 JSONL](rt6-endpoint-matrix-20261026/batch15/20261026T145020Z.jsonl). The
[evidence notes](rt6-endpoint-matrix-20261026/README.md) describe batch 15's TLS relay preflight and the failed setup
attempts that preceded it; those attempts are excluded from all counts, and each removed its tenant.

Neither run made a deployment, image, storage-format or application-code change.
