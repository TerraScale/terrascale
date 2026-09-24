# Logical index cap threshold probe

A local deterministic run increased the document count from the earlier 2,000-document campaign to 2,100. It used 30 samples, a two-index budget, 100 maintenance documents, the existing Release benchmark binary, and no Jev request. The environment removed `TERRABASE_JEV_API_KEY`.

| Run | Documents | Default collection logical cap | Outcome | Evidence |
| --- | ---: | ---: | --- | --- |
| Earlier deterministic campaign | 2,000 | 1,048,576 B | Three complete trials; 2 active indexes; 1,004,100 B logical rows in each | [campaign summary](../query-independent-deterministic-20260923T165616Z/summary.md) |
| Threshold probe | 2,100 | 1,048,576 B | Snapshot batch refused by precommit logical admission; benchmark exited 1 before producing comparison rows | [run receipt](run-receipt.json) |

The refusal occurred in `DurableIndexStore.AppendSnapshotBatchAsync` before its transaction commit. This probe confirms that the over-cap workload reaches the admission boundary; the benchmark runner does not retain the partial index root, so this artifact cannot establish cursor stability or recovered byte totals. The focused TUnit admission and recovery regressions cover those invariants. The 2,000-document campaign used a separate binary and is context for the threshold, not a controlled latency comparison.
