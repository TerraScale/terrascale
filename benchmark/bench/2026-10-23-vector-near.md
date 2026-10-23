# Brute-force near-sort query latency

Date: 2026-10-23T03:07:54Z. Region gru of a local two-region TerraBase cluster (tests/TerraBase.E2E BenchmarkClusterHost, Release), client on the same host. Random float32 vectors, limit 10, no filter, end-to-end HTTP latency per query, a new query vector each repeat (no result-cache hit).

| Documents | Dims | Metric | Repeat | Latency ms | Result |
| ---: | ---: | --- | ---: | ---: | --- |
| 10000 | 128 | cosine | 1 | 1423.3 | ok |
| 10000 | 128 | cosine | 2 | 1632.0 | ok |
| 10000 | 128 | cosine | 3 | 1973.1 | ok |
| 10000 | 128 | l2 | 1 | 1447.7 | ok |
| 10000 | 128 | l2 | 2 | 705.6 | ok |
| 10000 | 128 | l2 | 3 | 753.2 | ok |
| 100000 | 128 | cosine | 1 | 7898.6 | ok |
| 100000 | 128 | cosine | 2 | 7380.1 | ok |
| 100000 | 128 | cosine | 3 | 8531.8 | ok |
| 100000 | 128 | l2 | 1 | 8179.5 | ok |
| 100000 | 128 | l2 | 2 | 7189.9 | ok |
| 100000 | 128 | l2 | 3 | 6901.7 | ok |

Baseline on the same collections, same host: a full-scan filter query with no near and no match
(`{"filter":{"eq":["/n",-1]}}`) took 411 and 625 ms at 10000 documents, and 4462, 4695 and 5505 ms at 100000.
Near adds the per-row `tb_near` walk on top of the engine's existing per-row scan cost.

Caveats:

- mariogk-dev, 46 CPUs, shared with other builds (load average 17 to 59 during the run); the server ran at `nice -n 19`.
- The test harness freezes the query-deadline clock, so these runs never hit the 5-second deadline. In production a
  query beyond it answers 400 `invalid_query` (query too expensive): on this host that is every 100000-document scan,
  near or not. The work budget (5 M units, 12 per candidate here) allows about 400000 candidates.
- Repeats use a new vector each time; a repeated identical query answers from the query result cache.

Command: start `tests/TerraBase.E2E` `BenchmarkClusterHost` with `TERRABASE_BENCH_REGIONS=gru,sjc`, then
`benchmark/bin/vector-bench --targets <cluster.json> --sizes 10000,100000 --dims 128 --repeats 3 --out <file>`.
