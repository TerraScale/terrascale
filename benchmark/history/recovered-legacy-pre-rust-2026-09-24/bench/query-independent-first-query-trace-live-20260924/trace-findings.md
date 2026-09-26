# Real first-query ActivitySource trace

This run enabled `TERRABASE_FIRST_QUERY_TRACE=1` on the live TerraBase subject and used the same deterministic `status` candidate in fresh 0-update and 100-update roots. Each cold query had empty prior query history, published one observation, and returned the exact same 21 expected IDs. The raw receipt contains all nested spans and exact IDs; the [run receipt](run-receipt.json) records the source and runner digests.

| Cold lane | Full query span | Index inspection total | Stale-tail estimate | Planner explain | Selected execution | Physical branch | Observation publication | Actual access / rows |
|---|---:|---:|---:|---:|---:|---|---:|---|
| 0 updates | 70.027 ms | 0.387 ms | — | 7.639 ms | 15.559 ms | point seek 11.210 ms; 21 document reads | 5.956 ms | EqualitySeek / 42 |
| 100 updates | 80.579 ms | 0.201 ms | 0.182 ms; preferred fallback | 0.153 ms | 46.864 ms | source snapshot fallback inside selected index candidate | 0.057 ms | Scan / 2,100 |

The 100-update cold query used a source scan because the stale-tail estimate preferred fallback. `planner.selected_execute` includes the source executor call; its `execution.source_selected=False` tag means the planner selected an index candidate, while `execution.snapshot_fallback=True` records that the candidate used its source fallback. The cold query did **not** reproduce the earlier approximately 1-second latency. This trace therefore attributes this invocation's 80.579 ms path and does not identify the exact internal phase of those earlier slow calls. In the separate 100-update diagnostic root, an external standalone planner Explain took 681.260 ms while background catch-up raced; that timing is not part of the cold query trace.

| Span | Boundary and interpretation |
|---|---|
| `automatic_index.query` | Root for one runtime `QueryBoundedAsync` call. Child spans are included in its duration. |
| `query.admission` | Wait to acquire the runtime query lease. Added after this archived trace; the archived root started after admission, so its duration cannot rule out an admission wait in that run. |
| `index.inspect.initial`, `index.inspect.final` | Per-index physical validation before and after stale-tail handling. `index.ordinal` is local to one query. |
| `index.stale_tail.estimate`, `index.stale_tail.drain` | Runtime tail check and, when chosen, synchronous drain. |
| `planner.explain`, `planner.selected_execute` | Plan selection and selected candidate or source execution. The selected candidate may itself use a source fallback. |
| `physical.stale_tail.drain` | Query-service catch-up, when required. |
| `physical.point_seek`, `physical.range_seek`, `physical.iterator` | Exact physical branch attempts; each reports whether it served and its bounded entry and document read counters. |
| `query.observation.publish` | Analyzer and replay-evidence publication under its gate. |

Activities are emitted only when an `ActivityListener` subscribes to `TerraBase.Indexing.Query`. Tags contain booleans, access enums, a per-query index ordinal, and numeric work counts. No tenant, document, predicate, candidate ID, or raw source commit appears in a span. The trace receipt keeps source commit and index watermark evidence separately. Process CPU and allocation counters in the benchmark receipt include concurrent work. The generated [phase table](first-query-phase.md) contains the exact per-span tags and independent diagnostic boundaries; those external diagnostic timings are not additive parts of the cold query.

After this receipt was captured, listener safety was hardened: query spans now start before admission, a `query.admission` child span measures the lease wait, and throwing listener callbacks are isolated from query execution. The archived [run receipt](run-receipt.json) and runner digest identify the earlier binary precisely; its 70.027 ms root begins after admission. The later TUnit `IndexQueryDiagnosticsTests.ThrowingActivityListenersDoNotChangeQueryOrAmbientActivity` covers throwing Sample, ActivityStarted, and ActivityStopped callbacks, verifies the returned document and observation count, and verifies that `Activity.Current` is restored. No later timed trace is claimed here.

| Provenance | Value |
|---|---|
| [Raw trace receipt](first-query-phase-receipt.json) SHA-256 | `169e1f0ad18d7dc4c5daed5122c326f0476811d04e9e6a920cf5c908bc63d990` |
| Subject commit | `47b235dcc78240a9eb5330e454a7bf3273ffcba9` |
| Subject working-tree digest | `5176370881406fc024dc0768e0e84a0ae2a511eea91ba150c20c46e2166658da` |
| Runner assembly SHA-256 | `2ec1fe5a9566bdf993e84b504e59b3ce1bf759ed1a8422c4ac8857af3d0540ef` |
