# First status query trace

This single targeted run attached `IndexQueryDiagnostics` to the actual first `status` query and retained the separate first-repeat diagnostic. It completed at 2026-09-24T05:49:40Z with exit code 0. The raw query receipt is [`query-independent-indexing-receipt.json`](query-independent-indexing-receipt.json); run metadata is in [`run-receipt.json`](run-receipt.json).

## Provenance

- Command: `--workload=query-independent --documents=2000 --samples=10 --maximum-indexes=2 --maintenance-documents=100 --physical-index-budget-mib=8 --von`.
- TerraBase commit: `47b235dcc78240a9eb5330e454a7bf3273ffcba9`; working-tree digest: `6ebe34d8906f2471ace3138e12e2f7718c318ffd61fa034a7bbdee41f7d7fabc`.
- SHA-256 for the Release runner, loaded TerraBase assembly, and ZoneTree assembly: `7f9bf5f952c429820fa9729fdc96b6a8067a1ae165d7d7ad2457b9b69f9f348c`, `7ad8fd7310e3a6d9665868d70a0e94f716c05ad88c235886dd03fc06d8cf021f`, and `67c60a6b72ccd274b136ea3bd16c93badad73f0c47acbf6cb83d38b25b3a7844`.
- Benchmark Release build succeeded with 0 warnings and 0 errors. `git diff --check` passed in `ts-benchmark`.
- Run receipt SHA-256: `e31380a79705153172084a45c8e6ee63cc66c9215fd578f18ee68b98359872da`. Raw workload receipt SHA-256: `6a0f71584506255b09a3d1cecb2fa02f8376a83813d3f28843fafefec6982bf2`.

## First status query spans

| Strategy | First query | Runtime access | Outer query span | Largest child spans |
| --- | ---: | --- | ---: | --- |
| Deterministic schema statistics | 45.413 ms | Scan; snapshot fallback | 44.137 ms | selected execute 34.403 ms; planner explain 3.014 ms |
| Deterministic fallback after Von attempt | 904.180 ms | Equality seek; no snapshot fallback | 902.930 ms | index inspect initial 901.604 ms; selected execute 0.376 ms; physical point seek 0.310 ms; planner explain 0.102 ms |

The slow first call is now directly captured inside `index.inspect.initial`, before planner selection and physical execution. In the Von-attempt lane, the source remained at commit 22; both indexes' physical store commit watermarks advanced from 21 to 22 during the query, while lifecycle applied and target positions remained 21. In the deterministic lane the same store-watermark advance occurred, but it fell back to a source scan. Its first repeat used an equality seek after the watermark had advanced.

The local advisor returned `von-1.2.0` evidence (823 input tokens, 7 output tokens), but deterministic fallback was selected. This run provides no Von recommendation or Von benefit claim.

## Attribution limits

The measured delay sits inside `index.inspect.initial`; the current span does not split its internal storage operation, background feed `ApplyFramesAsync`, or physical store-gate wait. Repository source inspection found no existing ActivitySource spans for feed application or store-gate acquisition, so separating those would require new TerraBase instrumentation and was outside this benchmark-only change. Query metrics in this run include ActivityListener callback overhead. The span locates the slow phase, but does not establish which internal wait or operation consumed it.
