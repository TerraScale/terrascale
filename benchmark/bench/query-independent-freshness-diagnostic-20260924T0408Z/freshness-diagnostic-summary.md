# Query-independent index freshness diagnostic

This is a separate diagnostic run; it does not alter the three completed campaign directories or receipts.

## Inputs and build

- Subject snapshot working-tree digest: 489e1bc0d39ff9ccaf0ac472e1b733aa852189eef46ccc4aee74030bdd78f597.
- TerraBase assembly loaded from the existing Release output; SHA-256: 2bb2f209fa4158132aa446f4168b4516d4540b1d51cc571c9324949c5cd06bbc.
- Diagnostic runner SHA-256: 6150f5ae4cc3d053b496a9be90b5734c288a84663d4af83e1be7bcad1c7e1b37.
- The diagnostic runner was built from a disposable copy under runner-source with the instrumentation shown by freshness-diagnostic.json; build succeeded with 0 warnings and 0 errors.
- Workload: 2,000 documents, 10 repeat samples, maximum 2 indexes, 100 post-build maintenance updates, 8 MiB physical index cap, Von attempt requested.
- Run status: complete, exit code 0; output: [run receipt](run-output-2000/run-receipt.json), [raw workload receipt](run-output-2000/query-independent-indexing-receipt.json).

## Candidate identity

Both lanes generated the same candidate set and deterministic fallback selected the same two recommendations:

| Candidate ID | Path | Recommended |
|---|---|---|
| 339c3c051f1687d48cc83ae88c30526eda17867065d5c7de492648aea2d1ca8d | value | no |
| a8e0e52ae0acec498974c2a6cc17568344d898fdb942265dfb66f40c6fbbfef7 | status | yes |
| e1cadcc0861966acf7e1b783a102a7c966ce3b2c1d4ca4f767594a5f8821a6c0 | profile.segment | yes |

No Von recommendation was accepted. The fallback recommendation IDs match the deterministic lane. This diagnostic does not have the Von raw-choice payload; the exposed reason is the combined uncertain-or-abstained category.

## State immediately before the first status query

In both lanes the source was at commit sequence 22. Both active, visible recommended indexes had target and lifecycle-applied position 21, and their physical applied positions were 21. They were therefore one commit behind the source cut.

| Lane | Source cut | Active state | Explain selection | Explain reason |
|---|---:|---|---|---|
| Deterministic schema statistics | 22 | status: lifecycle=Active, visible=True, record=21, physical=21, fresh=False; profile.segment: lifecycle=Active, visible=True, record=21, physical=21, fresh=False | Scan | No candidate satisfies the query shape and freshness contract. |
| Deterministic fallback after Von attempt | 22 | status: lifecycle=Active, visible=True, record=21, physical=21, fresh=False; profile.segment: lifecycle=Active, visible=True, record=21, physical=21, fresh=False | Scan | No candidate satisfies the query shape and freshness contract. |

The no-mutation ExplainAsync call filtered out the stale indexes for both plans. It returned Scan and the reason No candidate satisfies the query shape and freshness contract. The explain result reflects freshness at the instant it ran.

## Query execution after the explain

| Lane | First status query µs | Rows examined | Index served |
|---|---:|---:|---|
| Deterministic schema statistics | 944,155.2 | 42 | True |
| Deterministic fallback after Von attempt | 1,209.3 | 42 | True |

The runtime query then used the index in both lanes. That change from the pre-query Scan explain shows that the background feed can advance freshness between an explain sample and query execution. The 2,000-document diagnostic did not reproduce the campaign split, but it confirms that candidate definitions were identical and the observed access path is sensitive to index readiness timing. The slower inference call may give catch-up more time, but this diagnostic does not establish it as the sole cause.

The four first-query explain/state records for this run are preserved in [freshness-diagnostic-2000.json](freshness-diagnostic-2000.json); the separate 100-document smoke is preserved in [freshness-diagnostic-100.json](freshness-diagnostic-100.json). Both are also collected in [freshness-diagnostic.json](freshness-diagnostic.json). The instrumented runner and its source copy are under this directory.
