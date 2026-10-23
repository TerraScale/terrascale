# FTS API benchmark

Run the following inside the parent's **single coordinated heavy job**, after pushing the branch and building its
native Turso library and Release `tests/TerraBase.E2E` host. Starting another `ts-heavy-any` job for the same branch
supersedes the native/backend job; the benchmark worker must not start one independently.

```sh
nice -n 19 bash benchmark/scripts/fts-local.sh
```

The script reuses `BenchmarkClusterHost` and `replbench.Region`, starts two temporary local regions (gru/sjc), targets
gru, checks the benchmark oracle, and writes `benchmark/bench/2026-10-23-fts.md`. All child processes inherit the low
priority. It rejects targets whose kind is not `local`; production secrets and serving-region discovery are outside
this local experiment. Temporary test tokens are held in a private temporary directory and never copied to reports.

1. Seed a fresh collection for each of **three repeats per size** (10,000 and 100,000 documents by default).
2. Time the complete synchronous `POST …/indexes` backfill with `kind:"text"` and `fields:["article.body"]`; verify
   the response is active and the index list reports `kind:"text"`.
3. Warm up with a disjoint term and verify every matching ID, exact count, finite descending `item.score`, strict
   BM25 ordering between one/four term occurrences, and ascending ID ties.
4. Measure 128 distinct `POST …/query` requests, using `filter:{"search":["/article/body","topic…"]}` and
   `sort:[{"path":"$score","order":"desc"}]`, limit 10. Validate every response.

Every document has integer `n` and nested `article.body`: 32 words, 129 balanced alphabetic topic tokens, one/four
topic occurrences alternating every 129 documents, orchard filler. Fresh collections ensure every build is independent;
distinct query terms avoid result-cache hits. This measures warm-index HTTP requests, not cold disk or WAN latency.
The host's frozen query deadline is explicitly disclosed; it cannot prove production timeout behavior.

The report retains each raw build time, payload bytes, p50/p95/p99, sequential query throughput, and every individual
query latency. Throughput includes validation time; individual latencies stop after the HTTP response is read.
Failed requests are not retried or counted as successes. Completed repeats are saved incrementally; a complete run
has repeats 1–3 for both requested sizes. Alternate sizes: append `--sizes 1000,10000` (258–129000 allowed so the
warmup query can verify its entire matching set within the API's 1000-item page bound).

The wire contract uses RFC6901 string paths only. `$score` is a sort pseudo-path. Search terms are nonempty, at most
4096 UTF-8 bytes, with no NUL; these generated terms are plain tokens, so the benchmark makes no advanced Tantivy
syntax claims. A missing text index must return HTTP 400 `invalid_query`; backend integration tests cover rejection
and pagination contracts. This benchmark changes no storage version: **Needs format bump** belongs to the feature PR.

No measurements have been published until the real native/backend build runs this command successfully. Never fill
the report with estimated or fake HTTP results.
