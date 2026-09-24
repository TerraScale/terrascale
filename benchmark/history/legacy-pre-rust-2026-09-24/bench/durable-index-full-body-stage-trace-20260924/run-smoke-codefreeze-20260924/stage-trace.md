# DurableIndexStore full-body query stage trace

Generated 2026-09-24T08:17:29.2276603+00:00; mode=smoke, unqualified; source commit `811b705c092f16931bc3740ac59d375ad1bfbed0`; ActivitySource `TerraBase.Indexing.Query`.
Fixture: 1000 base + 20 maintenance documents, 4,096 payload bytes per document; index `by-status`; predicate `s17 = status`; result count 11.

| Measurement | p50 ms |
| --- | ---: |
| Plain query wall time | 10.796 |
| Traced query wall time | 12.537 |
| Paired listener/instrumentation overhead estimate | 1.900 |
| Activity listener start overhead | 0.020296 |
| entry_seek | 0.114 |
| committed_document_read | 11.012 |
| decode_clone | 0.072 |
| predicate_recheck | 0.073 |
| sort | 0.004 |
| gate_wait | 0.000 |
| total | 12.041 |
| Sum of detailed work-stage p50 values | 11.274 |

Listener and instrumentation overhead is the median of paired traced-minus-plain wall-time samples and is reported separately; it is not subtracted from stage timings. Activity listener start cost is also reported from `query.observer.start_ns`. The raw JSON contains each pair and aggregate query span tags.

## Identity

Working tree dirty: `True`. Harness and product source hashes, loaded assembly identities, and toolchain details are in `stage-trace-receipt.json`.
